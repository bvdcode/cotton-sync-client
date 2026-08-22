// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_AfterTemporaryFeedFailureRunsManualVfsRecovery()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_828,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            remoteChanges.ReadFailures.Enqueue(
                new RemoteChangeFeedUnavailableException(new HttpRequestException("404 page not found")));
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            Assert.ThrowsAsync<RemoteChangeFeedUnavailableException>(
                async () => await work.RunOnceAsync(
                    syncPair,
                    SyncRunRequest.ForFull(SyncRunCause.Periodic)));
            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.Manual));

            Assert.Multiple(() =>
            {
                Assert.That(remoteChanges.ReadSyncPairIds, Has.Count.EqualTo(2));
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(inner.LastRequest?.Causes, Is.EqualTo(SyncRunCause.Manual));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithEmptyVfsFeedRunsLocalWatcherRecovery()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_828,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.LocalWatcherError));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(inner.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalWatcherError));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }
    }
}
