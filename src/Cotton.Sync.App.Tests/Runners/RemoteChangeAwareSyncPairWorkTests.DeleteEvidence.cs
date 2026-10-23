// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        [TestCase(SyncPairMode.FullMirror)]
        [TestCase(SyncPairMode.WindowsVirtualFiles)]
        public async Task RunOnceAsync_PassesDeleteEvidenceThenAdvancesToNewChanges(SyncPairMode mode)
        {
            SyncPairSettings pair = CreateSyncPair(mode);
            FakeSyncPairWork inner = new();
            SyncChangeDto deletion = new()
            {
                Id = 11, Kind = SyncChangeKind.FolderDeleted, Name = "Archive",
                ItemId = Guid.NewGuid(), ParentNodeId = pair.RemoteRootNodeId, LayoutId = Guid.NewGuid(),
            };
            RemoteChangeFeedBatch deleted = new(pair.Id.ToString("D"), 10, 11, false, false, 1, [deletion]);
            RemoteChangeFeedBatch created = new(pair.Id.ToString("D"), 11, 12, false, false, 1,
                CreateFileChanges(12, 1, pair.RemoteRootNodeId));
            FakeRemoteChangeFeedReader feed = new(deleted, created);
            RemoteChangeAwareSyncPairWork work = new(inner, feed, new FakeSyncStateStore());

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));
            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(2));
                Assert.That(inner.Requests[0].RemoteChanges.ContainsDeletes, Is.True);
                Assert.That(inner.Requests[0].RemoteChanges.Changes.Single().NodeId, Is.EqualTo(deletion.ItemId));
                Assert.That(inner.Requests[1].RemoteChanges.ContainsDeletes, Is.False);
                Assert.That(inner.Requests[1].RemoteChanges.Changes.Single().Cursor, Is.EqualTo(12));
                Assert.That(feed.AcknowledgedBatches, Is.EqualTo(new[] { deleted, created }));
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotUseExpiredDeleteEvidence()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            RemoteChangeFeedBatch batch = new(pair.Id.ToString("D"), 10, 20, false, true, 20,
            [
                new SyncChangeDto
                {
                    Id = 11, Kind = SyncChangeKind.FolderDeleted, Name = "Archive",
                    ItemId = Guid.NewGuid(), ParentNodeId = pair.RemoteRootNodeId, LayoutId = Guid.NewGuid(),
                },
            ]);
            FakeRemoteChangeFeedReader feed = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, feed, new FakeSyncStateStore());

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.LastRequest?.RemoteChanges.IsEmpty, Is.True);
                Assert.That(feed.FullResyncAcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }
    }
}
