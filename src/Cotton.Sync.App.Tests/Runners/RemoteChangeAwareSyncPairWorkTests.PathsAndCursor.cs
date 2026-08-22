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
    }
}
