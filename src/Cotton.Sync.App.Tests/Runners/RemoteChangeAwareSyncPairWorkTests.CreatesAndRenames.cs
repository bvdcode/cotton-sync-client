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
        public async Task RunOnceAsync_AcknowledgesRemoteBatchAfterInnerWorkSucceeds()
        {
            SyncPairSettings syncPair = CreateSyncPair();
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>()));
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.ReadSyncPairIds, Is.EqualTo(new[] { syncPair.Id.ToString("D") }));
                Assert.That(remoteChanges.AcknowledgedBatches, Has.Count.EqualTo(1));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [TestCase(SyncRunCause.Periodic)]
        [TestCase(SyncRunCause.Resume)]
        public async Task RunOnceAsync_WithWindowsVirtualFilesSkipsFullPassWhenFeedIsEmpty(SyncRunCause cause)
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>()));
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(cause));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesMergedPeriodicAndLocalRequestWhenFeedIsEmpty()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            RemoteChangeFeedBatch batch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest
                .ForFull(SyncRunCause.Periodic)
                .Merge(SyncRunRequest.ForLocalChangedPaths(["Docs/first.txt", "Docs/second.txt"]));

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.Causes, Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.LocalChange));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Docs/first.txt", "Docs/second.txt" }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRunsManualFullForMappedRemoteFileCreate()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            Guid remoteFileId = Guid.NewGuid();
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = remoteFileId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "remote-origin.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.Manual | SyncRunCause.RealtimeRemoteChange));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "remote-origin.txt" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(stateStore.LastRemoteNodeIds, Does.Contain(syncPair.RemoteRootNodeId));
                Assert.That(stateStore.LastRemoteFileIds, Does.Contain(remoteFileId));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesPeriodicRemoteFileCreate()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            Guid remoteFileId = Guid.NewGuid();
            RemoteChangeFeedBatch batch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = remoteFileId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "remote-origin.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.RealtimeRemoteChange));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "remote-origin.txt" }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRealtimeRemoteFileCreate()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            Guid remoteFileId = Guid.NewGuid();
            RemoteChangeFeedBatch batch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = remoteFileId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "remote-origin.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.Causes, Is.EqualTo(SyncRunCause.RealtimeRemoteChange));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "remote-origin.txt" }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }
    }
}
