// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_CompletedFullReconcileAcknowledgesDespitePendingMetadata(bool expiredCursor)
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            SyncFileMetadataPendingException failure = new("Cloud file metadata remains pending.");
            FakeSyncPairWork inner = new() { OnRunAsync = (_, _) => Task.FromException(failure) };
            FakeSyncStateStore state = new()
            {
                Cursor = new SyncChangeCursor { HasCompletedFullReconcile = false },
            };
            RemoteChangeFeedBatch batch = new(pair.Id.ToString("D"), 0, 42, false, expiredCursor, null, []);
            FakeRemoteChangeFeedReader feed = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, feed, state);

            Assert.That(Assert.ThrowsAsync<SyncFileMetadataPendingException>(
                async () => await work.RunOnceAsync(pair)), Is.SameAs(failure));

            Assert.That(state.Cursor.HasCompletedFullReconcile, Is.True);
            if (expiredCursor)
            {
                Assert.That(feed.FullResyncAcknowledgedBatches, Is.EqualTo(new[] { batch }));
                Assert.That(feed.AcknowledgedBatches, Is.Empty);
            }
            else
            {
                Assert.That(feed.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
                Assert.That(feed.FullResyncAcknowledgedBatches, Is.Empty);
            }
        }

        [Test]
        public async Task RunOnceAsync_PendingConversionDoesNotDiscardExactDeleteApproval()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore state = new(new SyncStateEntry
            {
                SyncPairId = pair.Id.ToString("D"),
                RelativePath = "pending.mp4",
                Kind = SyncEntryKind.File,
                RemoteNodeId = pair.RemoteRootNodeId,
                RemoteFileId = Guid.NewGuid(),
                RemoteFileManifestId = Guid.NewGuid(),
                LocalSizeBytes = 1,
                LocalLastWriteUtc = DateTime.UtcNow,
                LocalContentHash = "ab",
                RemoteContentHash = "ab",
            });
            FakeRemoteChangeFeedReader feed = new(new RemoteChangeFeedBatch(
                pair.Id.ToString("D"), 42, 42, false, false, null, []));
            RemoteChangeAwareSyncPairWork work = new(inner, feed, state);
            RemoteDeletePlanApproval approval = new(101, new string('a', 64));

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Manual, approval));

            Assert.That(inner.LastRequest!.ApprovedRemoteDeletePlan, Is.SameAs(approval));
            Assert.That(inner.LastRequest.LocalChangedPaths, Does.Contain("pending.mp4"));
        }

        [Test]
        public void RunOnceAsync_WithoutDurableStateDoesNotAcknowledgeMetadataFailure()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            SyncFileMetadataPendingException failure = new("Pending conversion.");
            FakeSyncPairWork inner = new() { OnRunAsync = (_, _) => Task.FromException(failure) };
            FakeRemoteChangeFeedReader feed = new(new RemoteChangeFeedBatch(
                pair.Id.ToString("D"), 0, 42, false, false, null, []));
            RemoteChangeAwareSyncPairWork work = new(inner, feed);

            Assert.ThrowsAsync<SyncFileMetadataPendingException>(async () => await work.RunOnceAsync(pair));
            Assert.That(feed.AcknowledgedBatches, Is.Empty);
            Assert.That(feed.FullResyncAcknowledgedBatches, Is.Empty);
        }
    }
}
