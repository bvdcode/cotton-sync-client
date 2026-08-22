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
    public class RemoteChangeAwareSyncPairWorkTests
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

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFileDeleteWithoutExistingState()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            Guid remoteFileId = Guid.NewGuid();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
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
                        Kind = SyncChangeKind.FileDeleted,
                        LayoutId = Guid.NewGuid(),
                        ItemId = remoteFileId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "deleted.txt",
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
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "deleted.txt" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFolderDeleteWithoutExistingState()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
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
                        Kind = SyncChangeKind.FolderDeleted,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "DeletedFolder",
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
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "DeletedFolder" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFolderDeleteToTrackedSubtree()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            Guid folderId = Guid.NewGuid();
            Guid subFolderId = Guid.NewGuid();
            Guid rootFileId = Guid.NewGuid();
            Guid childFileId = Guid.NewGuid();
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "DeletedFolder",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = folderId,
                },
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "DeletedFolder/Sub",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = subFolderId,
                },
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "DeletedFolder/root.txt",
                    Kind = SyncEntryKind.File,
                    RemoteNodeId = folderId,
                    RemoteFileId = rootFileId,
                },
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "DeletedFolder/Sub/child.txt",
                    Kind = SyncEntryKind.File,
                    RemoteNodeId = subFolderId,
                    RemoteFileId = childFileId,
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
                        Kind = SyncChangeKind.FolderDeleted,
                        LayoutId = Guid.NewGuid(),
                        ItemId = folderId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "DeletedFolder",
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
                    Is.EquivalentTo(new[]
                    {
                        "DeletedFolder",
                        "DeletedFolder/Sub",
                        "DeletedFolder/root.txt",
                        "DeletedFolder/Sub/child.txt",
                    }));
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(stateStore.PathPrefixLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFileRenameWithoutExistingState()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
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
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "renamed.txt",
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
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "renamed.txt" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFolderMoveWithoutExistingState()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
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
                        Kind = SyncChangeKind.FolderMoved,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "MovedFolder",
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
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "MovedFolder" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRunsManualFullForChangeOutsideSyncPair()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
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
                        Kind = SyncChangeKind.FolderCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = Guid.NewGuid(),
                        Name = "Nested",
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
                Assert.That(inner.LastRequest?.Causes, Is.EqualTo(SyncRunCause.Manual));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesIgnoresRemoteRootCreationDuringManualFull()
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
                        Name = "qa-root",
                        CreatedAt = DateTime.UtcNow,
                    },
                    new SyncChangeDto
                    {
                        Id = 12,
                        Kind = SyncChangeKind.FolderCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "pre-existing",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "pre-existing" }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public void RunOnceAsync_WithWindowsVirtualFilesRejectsRelatedInvalidPathWithoutFullRun()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            Guid folderId = Guid.NewGuid();
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Existing",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = folderId,
                });
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
                        Kind = SyncChangeKind.FolderRenamed,
                        LayoutId = Guid.NewGuid(),
                        ItemId = folderId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "../Invalid",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);

            SyncActionRequiredException? exception = Assert.ThrowsAsync<SyncActionRequiredException>(
                () => work.RunOnceAsync(syncPair));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(exception?.Message, Does.Contain("could not be mapped"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMergesScopedLocalAndRemoteRequests()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
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
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "remote-origin.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(
                    inner.LastRequest?.LocalChangedPaths,
                    Is.EquivalentTo(new[] { "Docs/report.txt", "remote-origin.txt" }));
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.RealtimeRemoteChange));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithCompletedVfsReconcileSkipsPeriodicFullSyncOnEmptyFeed()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_828,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithCompletedVfsReconcileRunsSafetyFullSyncOnEmptyFeed()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            stateStore.Cursor = new SyncChangeCursor
            {
                SyncPairId = syncPair.Id.ToString("D"),
                LastCursor = 9_828,
                HasCompletedFullReconcile = true,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            RemoteChangeFeedBatch batch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_828,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest.ForFull(
                SyncRunCause.Periodic | SyncRunCause.InternalMaintenance);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest, Is.SameAs(request));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_AfterOneDayOfEmptyPeriodicChecksRunsLocalChangeWithoutFullScanDebt()
        {
            const int periodicCheckCount = 144;
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            RemoteChangeFeedBatch[] batches = Enumerable
                .Range(0, periodicCheckCount + 1)
                .Select(_ => new RemoteChangeFeedBatch(
                    syncPair.Id.ToString("D"),
                    sinceCursor: 9_828,
                    nextCursor: 9_828,
                    hasMore: false,
                    cursorExpired: false,
                    earliestAvailableCursor: 5,
                    changes: Array.Empty<SyncChangeDto>()))
                .ToArray();
            FakeRemoteChangeFeedReader remoteChanges = new(batches);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);

            for (int check = 0; check < periodicCheckCount; check++)
            {
                await work.RunOnceAsync(
                    syncPair,
                    SyncRunRequest.ForFull(SyncRunCause.Periodic));
            }

            SyncRunRequest localChange = SyncRunRequest.ForLocalChangedPaths(["Pictures/album/photo.jpg"]);
            await work.RunOnceAsync(syncPair, localChange);

            Assert.Multiple(() =>
            {
                Assert.That(remoteChanges.ReadSyncPairIds, Has.Count.EqualTo(periodicCheckCount + 1));
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(localChange.LocalChangedPaths));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithIncompleteVfsReconcileForcesFullRunOnEmptyFeed()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.Cursor = new SyncChangeCursor
            {
                SyncPairId = syncPair.Id.ToString("D"),
                LastCursor = 9_828,
                HasCompletedFullReconcile = false,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_828,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.Resume | SyncRunCause.InitialPopulation));
                Assert.That(stateStore.Cursor.HasCompletedFullReconcile, Is.True);
                Assert.That(stateStore.Cursor.LastCursor, Is.EqualTo(9_828));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithIncompleteVfsReconcileAndRemoteChangesKeepsFullRun()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.Cursor = new SyncChangeCursor
            {
                SyncPairId = syncPair.Id.ToString("D"),
                LastCursor = 9_828,
                HasCompletedFullReconcile = false,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_829,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 9_829,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "remote-origin.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.Resume | SyncRunCause.InitialPopulation));
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.Empty);
                Assert.That(stateStore.Cursor.HasCompletedFullReconcile, Is.True);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public void RunOnceAsync_WhenIncompleteVfsReconcileFailsKeepsRecoveryPending()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork
            {
                ThrowOnRun = true,
            };
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.Cursor = new SyncChangeCursor
            {
                SyncPairId = syncPair.Id.ToString("D"),
                LastCursor = 9_828,
                HasCompletedFullReconcile = false,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            RemoteChangeFeedBatch batch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 9_828,
                nextCursor: 9_828,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(batch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await work.RunOnceAsync(
                    syncPair,
                    SyncRunRequest.ForFull(SyncRunCause.Resume)));
            Assert.Multiple(() =>
            {
                Assert.That(stateStore.Cursor.HasCompletedFullReconcile, Is.False);
                Assert.That(stateStore.SavedCursors, Is.Empty);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithEmptyVfsFeedSkipsRealtimeFullSync()
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
                SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
            });
        }

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

        [Test]
        public void RunOnceAsync_WithWindowsVirtualFilesReportsUnresolvedRemotePathWithoutFullFallback()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            Guid folderId = Guid.NewGuid();
            FakeSyncStateStore stateStore = new(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Existing",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = folderId,
                });
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
                        Kind = SyncChangeKind.FolderRenamed,
                        LayoutId = Guid.NewGuid(),
                        ItemId = folderId,
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "../Invalid",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Recordings/subtitle.srt"]);

            SyncActionRequiredException? exception = Assert.ThrowsAsync<SyncActionRequiredException>(
                () => work.RunOnceAsync(syncPair, request));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest, Is.SameAs(request));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(2));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(exception?.Message, Does.Contain("could not be mapped"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesKnownFolderMovedOutsidePairToOldPath()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            Guid folderId = Guid.NewGuid();
            Guid previousParentNodeId = Guid.NewGuid();
            FakeSyncStateStore stateStore = new(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Recordings",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = previousParentNodeId,
                },
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "Recordings/Archive",
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = folderId,
                });
            FakeSyncPairWork inner = new();
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
                        Kind = SyncChangeKind.FolderMoved,
                        LayoutId = Guid.NewGuid(),
                        ItemId = folderId,
                        ParentNodeId = Guid.NewGuid(),
                        PreviousParentNodeId = previousParentNodeId,
                        Name = "Archive",
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
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Recordings/Archive" }));
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesProcessesDelayedChangeAfterSkippedEmptyBatch()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            RemoteChangeFeedBatch emptyBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            RemoteChangeFeedBatch delayedBatch = new RemoteChangeFeedBatch(
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
                        Name = "remote-origin.txt",
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(emptyBatch, delayedBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, new FakeSyncStateStore());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));
            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { delayedBatch }));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRunsInitialFullWhenRemoteCursorIsZero()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 0,
                nextCursor: 0,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 0,
                changes: Array.Empty<SyncChangeDto>()));
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesPreservesScopedLocalRequestForEmptyRemoteBatch()
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
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest, Is.SameAs(request));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRunsWhenDrainedRemotePageHadChanges()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            RemoteChangeFeedBatch firstBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: true,
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
                        Name = "report.txt",
                    },
                ]);
            RemoteChangeFeedBatch secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 12,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, new FakeSyncStateStore());

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesProcessesChangeAfterEmptyIntermediatePage()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            RemoteChangeFeedBatch emptyPage = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            RemoteChangeFeedBatch changePage = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 12,
                nextCursor: 13,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 13,
                        Kind = SyncChangeKind.FileCreated,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = syncPair.RemoteRootNodeId,
                        Name = "remote-after-gap.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new(emptyPage, changePage);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "remote-after-gap.txt" }));
                Assert.That(
                    remoteChanges.ReadFromCursorRequests,
                    Is.EqualTo(new[] { (SyncPairId: syncPair.Id.ToString("D"), SinceCursor: 12L) }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { changePage }));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesChangesObservedBeforeFinalDrainedPage()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            RemoteChangeFeedBatch firstBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: true,
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
                        Name = "first-page.txt",
                        CreatedAt = DateTime.UtcNow,
                    },
                ]);
            RemoteChangeFeedBatch secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 12,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "first-page.txt" }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesRequestedSyncSurface()
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
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.LastRequest, Is.SameAs(request));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_DrainsRemotePagesBeforeSingleInnerWorkPass()
        {
            SyncPairSettings syncPair = CreateSyncPair();
            FakeSyncPairWork inner = new FakeSyncPairWork();
            RemoteChangeFeedBatch firstBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: true,
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
                        ParentNodeId = Guid.NewGuid(),
                        Name = "report.txt",
                    },
                ]);
            RemoteChangeFeedBatch secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 12,
                nextCursor: 14,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes:
                [
                    new SyncChangeDto
                    {
                        Id = 13,
                        Kind = SyncChangeKind.FolderRenamed,
                        LayoutId = Guid.NewGuid(),
                        ItemId = Guid.NewGuid(),
                        ParentNodeId = Guid.NewGuid(),
                        Name = "Archive",
                    },
                ]);
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.ReadSyncPairIds, Is.EqualTo(new[] { syncPair.Id.ToString("D") }));
                Assert.That(remoteChanges.ReadFromCursorRequests, Is.EqualTo(new[] { (SyncPairId: syncPair.Id.ToString("D"), SinceCursor: 12L) }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_BoundsRemoteFeedAccumulationAndLeavesLaterPagesPending()
        {
            SyncPairSettings syncPair = CreateSyncPair();
            FakeSyncPairWork inner = new();
            RemoteChangeFeedBatch firstBatch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 510,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(11, 500, syncPair.RemoteRootNodeId));
            RemoteChangeFeedBatch secondBatch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 510,
                nextCursor: 1_010,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(511, 500, syncPair.RemoteRootNodeId));
            RemoteChangeFeedBatch unreadBatch = new(
                syncPair.Id.ToString("D"),
                sinceCursor: 1_010,
                nextCursor: 1_011,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: CreateFileChanges(1_011, 1, syncPair.RemoteRootNodeId));
            FakeRemoteChangeFeedReader remoteChanges = new(firstBatch, secondBatch, unreadBatch);
            RemoteChangeAwareSyncPairWork work = new(inner, remoteChanges);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["local-edit.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(remoteChanges.ReadLimits, Is.EqualTo(new[] { 500, 500 }));
                Assert.That(
                    remoteChanges.ReadFromCursorRequests,
                    Is.EqualTo(new[] { (SyncPairId: syncPair.Id.ToString("D"), SinceCursor: 510L) }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
            });
        }

        [Test]
        public void RunOnceAsync_FailsWithoutAcknowledgementWhenRemoteFeedDoesNotAdvance()
        {
            SyncPairSettings syncPair = CreateSyncPair();
            FakeSyncPairWork inner = new FakeSyncPairWork();
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 10,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: null,
                changes: Array.Empty<SyncChangeDto>()));
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            Assert.ThrowsAsync<InvalidOperationException>(() => work.RunOnceAsync(syncPair));
            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_AcknowledgesFullResyncWhenRemoteCursorExpired()
        {
            SyncPairSettings syncPair = CreateSyncPair();
            FakeSyncPairWork inner = new FakeSyncPairWork();
            RemoteChangeFeedBatch expiredBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 10,
                hasMore: false,
                cursorExpired: true,
                earliestAvailableCursor: 15,
                changes: Array.Empty<SyncChangeDto>());
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(expiredBatch);
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            SyncRunRequest scopedRequest = SyncRunRequest.ForLocalChangedPaths(["local-change.txt"]);
            await work.RunOnceAsync(syncPair, scopedRequest);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(
                    inner.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.RemoteCursorExpired));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.EqualTo(new[] { expiredBatch }));
            });
        }

        [Test]
        public void RunOnceAsync_DoesNotAcknowledgeWhenInnerWorkFails()
        {
            SyncPairSettings syncPair = CreateSyncPair();
            FakeSyncPairWork inner = new FakeSyncPairWork
            {
                ThrowOnRun = true,
            };
            FakeRemoteChangeFeedReader remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: null,
                changes: Array.Empty<SyncChangeDto>()));
            RemoteChangeAwareSyncPairWork work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            Assert.ThrowsAsync<InvalidOperationException>(() => work.RunOnceAsync(syncPair));
            Assert.Multiple(() =>
            {
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        private static SyncPairSettings CreateSyncPair(SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = mode,
            };
        }

        private class FakeSyncPairWork : ISyncPairWork
        {
            public int RunCallCount { get; private set; }

            public SyncRunRequest? LastRequest { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public bool ThrowOnRun { get; set; }

            public Func<int, SyncRunRequest, Task>? OnRunAsync { get; set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                RunCallCount++;
                LastRequest = request;
                Requests.Add(request);
                if (ThrowOnRun)
                {
                    throw new InvalidOperationException("Inner work failed.");
                }

                if (OnRunAsync is not null)
                {
                    await OnRunAsync(RunCallCount, request).ConfigureAwait(false);
                }
            }
        }

        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly List<SyncStateEntry> _entries;

            public FakeSyncStateStore(params SyncStateEntry[] entries)
            {
                _entries = [.. entries];
                Cursor = new SyncChangeCursor
                {
                    HasCompletedFullReconcile = true,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
            }

            public SyncChangeCursor Cursor { get; set; }

            public List<SyncChangeCursor> SavedCursors { get; } = [];

            public int LoadPairEntriesCallCount { get; private set; }

            public int RemoteIdLookupCallCount { get; private set; }

            public int PathPrefixLookupCallCount { get; private set; }

            public IReadOnlyList<Guid> LastRemoteNodeIds { get; private set; } = [];

            public IReadOnlyList<Guid> LastRemoteFileIds { get; private set; } = [];

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<SyncStateEntry> entries = _entries
                    .Where(entry => entry.SyncPairId == syncPairId)
                    .ToArray();
                return Task.FromResult(entries);
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCallCount++;
                await Task.Yield();
                foreach (SyncStateEntry entry in _entries.Where(entry => entry.SyncPairId == syncPairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                }
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByRemoteIdsAsync(
                string syncPairId,
                IEnumerable<Guid> remoteNodeIds,
                IEnumerable<Guid> remoteFileIds,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                RemoteIdLookupCallCount++;
                HashSet<Guid> nodeIds = remoteNodeIds.Where(static id => id != Guid.Empty).ToHashSet();
                HashSet<Guid> fileIds = remoteFileIds.Where(static id => id != Guid.Empty).ToHashSet();
                LastRemoteNodeIds = nodeIds.ToArray();
                LastRemoteFileIds = fileIds.ToArray();
                await Task.Yield();
                foreach (SyncStateEntry entry in _entries.Where(entry => entry.SyncPairId == syncPairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Kind == SyncEntryKind.Directory
                            && entry.RemoteNodeId.HasValue
                            && nodeIds.Contains(entry.RemoteNodeId.Value))
                        || (entry.Kind == SyncEntryKind.File
                            && entry.RemoteFileId.HasValue
                            && fileIds.Contains(entry.RemoteFileId.Value)))
                    {
                        yield return entry;
                    }
                }
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathPrefixAsync(
                string syncPairId,
                string relativePathPrefix,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                PathPrefixLookupCallCount++;
                string prefixKey = SyncPath.ToKey(relativePathPrefix);
                string childPrefix = prefixKey + "/";
                await Task.Yield();
                foreach (SyncStateEntry entry in _entries.Where(entry => entry.SyncPairId == syncPairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string entryKey = SyncPath.ToKey(entry.RelativePath);
                    if (string.Equals(entryKey, prefixKey, StringComparison.OrdinalIgnoreCase)
                        || entryKey.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return entry;
                    }
                }
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor
                {
                    SyncPairId = syncPairId,
                    LastCursor = Cursor.LastCursor,
                    CursorExpired = Cursor.CursorExpired,
                    EarliestAvailableCursor = Cursor.EarliestAvailableCursor,
                    HasCompletedFullReconcile = Cursor.HasCompletedFullReconcile,
                    UpdatedAtUtc = Cursor.UpdatedAtUtc,
                });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                SyncStateEntry? entry = _entries.SingleOrDefault(item =>
                    item.SyncPairId == syncPairId
                    && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item =>
                    item.SyncPairId == entry.SyncPairId
                    && string.Equals(item.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
                _entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                Cursor = new SyncChangeCursor
                {
                    SyncPairId = cursor.SyncPairId,
                    LastCursor = cursor.LastCursor,
                    CursorExpired = cursor.CursorExpired,
                    EarliestAvailableCursor = cursor.EarliestAvailableCursor,
                    HasCompletedFullReconcile = cursor.HasCompletedFullReconcile,
                    UpdatedAtUtc = cursor.UpdatedAtUtc,
                };
                SavedCursors.Add(Cursor);
                return Task.CompletedTask;
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item =>
                    item.SyncPairId == syncPairId
                    && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item => item.SyncPairId == syncPairId);
                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item => item.SyncPairId == syncPairId);
                _entries.AddRange(entries);
                return Task.CompletedTask;
            }
        }

        private class FakeRemoteChangeFeedReader : IRemoteChangeFeedReader
        {
            private readonly Queue<RemoteChangeFeedBatch> _batches;

            public FakeRemoteChangeFeedReader(params RemoteChangeFeedBatch[] batches)
            {
                _batches = new Queue<RemoteChangeFeedBatch>(batches);
            }

            public List<string> ReadSyncPairIds { get; } = [];

            public List<(string SyncPairId, long SinceCursor)> ReadFromCursorRequests { get; } = [];

            public List<int> ReadLimits { get; } = [];

            public List<RemoteChangeFeedBatch> AcknowledgedBatches { get; } = [];

            public List<RemoteChangeFeedBatch> FullResyncAcknowledgedBatches { get; } = [];

            public Queue<Exception> ReadFailures { get; } = [];

            public Task<RemoteChangeFeedBatch> ReadAsync(
                string syncPairId,
                int limit = RemoteChangeFeedDefaults.PageSize,
                CancellationToken cancellationToken = default)
            {
                ReadSyncPairIds.Add(syncPairId);
                ReadLimits.Add(limit);
                if (ReadFailures.TryDequeue(out Exception? failure))
                {
                    throw failure;
                }

                return Task.FromResult(_batches.Dequeue());
            }

            public Task<RemoteChangeFeedBatch> ReadFromCursorAsync(
                string syncPairId,
                long sinceCursor,
                int limit = RemoteChangeFeedDefaults.PageSize,
                CancellationToken cancellationToken = default)
            {
                ReadFromCursorRequests.Add((syncPairId, sinceCursor));
                ReadLimits.Add(limit);
                return Task.FromResult(_batches.Dequeue());
            }

            public Task AcknowledgeAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
            {
                AcknowledgedBatches.Add(batch);
                return Task.CompletedTask;
            }

            public Task AcknowledgeFullResyncAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
            {
                FullResyncAcknowledgedBatches.Add(batch);
                return Task.CompletedTask;
            }
        }

        private static IReadOnlyCollection<SyncChangeDto> CreateFileChanges(
            long firstCursor,
            int count,
            Guid parentNodeId)
        {
            return Enumerable
                .Range(0, count)
                .Select(index => new SyncChangeDto
                {
                    Id = firstCursor + index,
                    Kind = SyncChangeKind.FileCreated,
                    LayoutId = Guid.NewGuid(),
                    ItemId = Guid.NewGuid(),
                    ParentNodeId = parentNodeId,
                    Name = $"file-{firstCursor + index}.txt",
                })
                .ToArray();
        }
    }
}
