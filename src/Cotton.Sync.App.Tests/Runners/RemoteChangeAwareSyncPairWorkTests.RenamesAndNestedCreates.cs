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
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesMergedRealtimeAndLocalRequest()
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
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "Budget.xlsx",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest
                .ForFull(SyncRunCause.RealtimeRemoteChange)
                .Merge(SyncRunRequest.ForLocalChangedPaths(["Budget.xlsx", "Budget (1).xlsx"]));

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(
                    inner.LastRequest?.LocalChangedPaths,
                    Is.EqualTo(new[] { "Budget (1).xlsx", "Budget.xlsx" }));
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.RealtimeRemoteChange));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRunsInitialPopulationFullForUnmappedRemoteRootChange()
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
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FolderCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = syncPair.RemoteRootNodeId,
                        ParentNodeId = Guid.NewGuid(),
                        Name = "Documents",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest.ForFull(SyncRunCause.InitialPopulation);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest, Is.SameAs(request));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(inner.LastRequest?.Causes, Is.EqualTo(SyncRunCause.InitialPopulation));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.Empty);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteRenameToOldAndNewPaths()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            Guid remoteFileId = Guid.NewGuid();
            FakeSyncStateStore stateStore = new FakeSyncStateStore(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "old.txt",
                    Kind = SyncEntryKind.File,
                    RemoteNodeId = syncPair.RemoteRootNodeId,
                    RemoteFileId = remoteFileId,
                });
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
                        Kind = SyncChangeKind.FileRenamed,
                        LayoutId = Guid.NewGuid(),
                        ItemId = remoteFileId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "new.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EquivalentTo(new[] { "old.txt", "new.txt" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesNestedCreatesFromSameRemoteBatch()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            Guid parentFolderId = Guid.NewGuid();
            Guid childFolderId = Guid.NewGuid();
            Guid fileId = Guid.NewGuid();
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 14,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FolderCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = parentFolderId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "Parent",
                        CreatedAt = DateTime.UtcNow,
                    },
                    new SyncChangeDto
                    {
                        Id = 12,
                        Kind = SyncChangeKind.FolderCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = childFolderId,
                        ParentNodeId = parentFolderId,
                        Name = "Child",
                        CreatedAt = DateTime.UtcNow,
                    },
                    new SyncChangeDto
                    {
                        Id = 13,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = fileId,
                        ParentNodeId = childFolderId,
                        Name = "remote.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(
                    inner.LastRequest?.LocalChangedPaths,
                    Is.EquivalentTo(new[] { "Parent", "Parent/Child", "Parent/Child/remote.txt" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesChildChangeAfterFolderRenameInSameBatch()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            Guid folderId = Guid.NewGuid();
            Guid fileId = Guid.NewGuid();
            FakeSyncStateStore stateStore = new(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Old",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = folderId,
                });
            FakeSyncPairWork inner = new();
            RemoteChangeFeedBatch batch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 13,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 11,
                        Kind = SyncChangeKind.FolderRenamed,
                        LayoutId = Guid.NewGuid(),
                        ItemId = folderId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "New",
                        CreatedAt = DateTime.UtcNow,
                    },
                    new SyncChangeDto
                    {
                        Id = 12,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = fileId,
                        ParentNodeId = folderId,
                        Name = "child.txt",
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
                    inner.LastRequest?.LocalChangedPaths,
                    Is.EquivalentTo(new[] { "Old", "New", "New/child.txt" }));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Does.Not.Contain("Old/child.txt"));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

    }
}
