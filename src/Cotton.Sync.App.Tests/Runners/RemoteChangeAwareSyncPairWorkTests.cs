// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

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
            var syncPair = CreateSyncPair();
            var inner = new FakeSyncPairWork();
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.ReadSyncPairIds, Is.EqualTo(new[] { syncPair.Id.ToString("D") }));
                Assert.That(remoteChanges.AcknowledgedBatches, Has.Count.EqualTo(1));
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesSkipsInnerFullRunWithoutAcknowledgingEmptyBatch()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.Zero);
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFileCreateFromChangeFeed()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            Guid remoteFileId = Guid.NewGuid();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(inner.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "remote-origin.txt" }));
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(stateStore.LastRemoteNodeIds, Does.Contain(syncPair.RemoteRootNodeId));
                Assert.That(stateStore.LastRemoteFileIds, Does.Contain(remoteFileId));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteRenameToOldAndNewPaths()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            Guid remoteFileId = Guid.NewGuid();
            var stateStore = new FakeSyncStateStore(
                new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = "old.txt",
                    Kind = SyncEntryKind.File,
                    RemoteNodeId = syncPair.RemoteRootNodeId,
                    RemoteFileId = remoteFileId,
                });
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            Guid parentFolderId = Guid.NewGuid();
            Guid childFolderId = Guid.NewGuid();
            Guid fileId = Guid.NewGuid();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFileDeleteWithoutExistingState()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            Guid remoteFileId = Guid.NewGuid();
            var stateStore = new FakeSyncStateStore();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesRemoteFileRenameWithoutExistingState()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
        public async Task RunOnceAsync_WithWindowsVirtualFilesKeepsFullRunWhenRemotePathCannotBeResolved()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.True);
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.Zero);
                Assert.That(stateStore.RemoteIdLookupCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMergesScopedLocalAndRemoteRequests()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            var batch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(batch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest?.IsFull, Is.False);
                Assert.That(
                    inner.LastRequest?.LocalChangedPaths,
                    Is.EquivalentTo(new[] { "Docs/report.txt", "remote-origin.txt" }));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesProcessesDelayedChangeAfterSkippedEmptyBatch()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var emptyBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            var delayedBatch = new RemoteChangeFeedBatch(
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
                        ParentNodeId = Guid.NewGuid(),
                        Name = "remote-origin.txt",
                    },
                ]);
            var remoteChanges = new FakeRemoteChangeFeedReader(emptyBatch, delayedBatch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);
            await work.RunOnceAsync(syncPair);

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
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 0,
                nextCursor: 0,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 0,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

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
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);
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
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var firstBatch = new RemoteChangeFeedBatch(
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
            var secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 12,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            var remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.EqualTo(new[] { secondBatch }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesScopesChangesObservedBeforeFinalDrainedPage()
        {
            var syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            var inner = new FakeSyncPairWork();
            var stateStore = new FakeSyncStateStore();
            var firstBatch = new RemoteChangeFeedBatch(
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
            var secondBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 12,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>());
            var remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges, stateStore);

            await work.RunOnceAsync(syncPair);

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
            var syncPair = CreateSyncPair();
            var inner = new FakeSyncPairWork();
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: 5,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);
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
            var syncPair = CreateSyncPair();
            var inner = new FakeSyncPairWork();
            var firstBatch = new RemoteChangeFeedBatch(
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
            var secondBatch = new RemoteChangeFeedBatch(
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
            var remoteChanges = new FakeRemoteChangeFeedReader(firstBatch, secondBatch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

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
        public void RunOnceAsync_FailsWithoutAcknowledgementWhenRemoteFeedDoesNotAdvance()
        {
            var syncPair = CreateSyncPair();
            var inner = new FakeSyncPairWork();
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 10,
                hasMore: true,
                cursorExpired: false,
                earliestAvailableCursor: null,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

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
            var syncPair = CreateSyncPair();
            var inner = new FakeSyncPairWork();
            var expiredBatch = new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 10,
                hasMore: false,
                cursorExpired: true,
                earliestAvailableCursor: 15,
                changes: Array.Empty<SyncChangeDto>());
            var remoteChanges = new FakeRemoteChangeFeedReader(expiredBatch);
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.AcknowledgedBatches, Is.Empty);
                Assert.That(remoteChanges.FullResyncAcknowledgedBatches, Is.EqualTo(new[] { expiredBatch }));
            });
        }

        [Test]
        public void RunOnceAsync_DoesNotAcknowledgeWhenInnerWorkFails()
        {
            var syncPair = CreateSyncPair();
            var inner = new FakeSyncPairWork
            {
                ThrowOnRun = true,
            };
            var remoteChanges = new FakeRemoteChangeFeedReader(new RemoteChangeFeedBatch(
                syncPair.Id.ToString("D"),
                sinceCursor: 10,
                nextCursor: 12,
                hasMore: false,
                cursorExpired: false,
                earliestAvailableCursor: null,
                changes: Array.Empty<SyncChangeDto>()));
            var work = new RemoteChangeAwareSyncPairWork(inner, remoteChanges);

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

            public bool ThrowOnRun { get; set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                RunCallCount++;
                LastRequest = request;
                if (ThrowOnRun)
                {
                    throw new InvalidOperationException("Inner work failed.");
                }

                return Task.CompletedTask;
            }
        }

        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly List<SyncStateEntry> _entries;

            public FakeSyncStateStore(params SyncStateEntry[] entries)
            {
                _entries = [.. entries];
            }

            public int LoadPairEntriesCallCount { get; private set; }

            public int RemoteIdLookupCallCount { get; private set; }

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
                    LastCursor = 0,
                    UpdatedAtUtc = DateTime.UtcNow,
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

            public List<RemoteChangeFeedBatch> AcknowledgedBatches { get; } = [];

            public List<RemoteChangeFeedBatch> FullResyncAcknowledgedBatches { get; } = [];

            public Task<RemoteChangeFeedBatch> ReadAsync(
                string syncPairId,
                int limit = RemoteChangeFeedDefaults.PageSize,
                CancellationToken cancellationToken = default)
            {
                ReadSyncPairIds.Add(syncPairId);
                return Task.FromResult(_batches.Dequeue());
            }

            public Task<RemoteChangeFeedBatch> ReadFromCursorAsync(
                string syncPairId,
                long sinceCursor,
                int limit = RemoteChangeFeedDefaults.PageSize,
                CancellationToken cancellationToken = default)
            {
                ReadFromCursorRequests.Add((syncPairId, sinceCursor));
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
    }
}
