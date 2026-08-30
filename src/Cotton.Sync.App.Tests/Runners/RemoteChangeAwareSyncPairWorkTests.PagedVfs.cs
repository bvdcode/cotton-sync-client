// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Models.Enums;
using Cotton.Sync;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesSkipsBoundedOutsidePairBacklog()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            Guid outsideParentNodeId = Guid.NewGuid();
            RemoteChangeFeedBatch firstBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 510,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(11, 500, outsideParentNodeId));
            RemoteChangeFeedBatch secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 510,
                nextCursor: 1_010,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(511, 500, outsideParentNodeId));
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(remoteChanges.ReadLimits, Is.EqualTo(new[] { 500, 500 }));
                Assert.That(
                    remoteChanges.ReadFromCursorRequests,
                    Is.EqualTo(new[] { (SyncPairId: syncPair.Id.ToString("D"), SinceCursor: 510L) }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesBoundedInsidePairBacklog()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            RemoteChangeFeedBatch firstBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 510,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(11, 500, syncPair.RemoteRootNodeId));
            RemoteChangeFeedBatch secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 510,
                nextCursor: 1_010,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(511, 500, syncPair.RemoteRootNodeId));
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Has.Count.EqualTo(1_000));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Does.Contain("file-11.txt"));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Does.Contain("file-1010.txt"));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }
    }
}
