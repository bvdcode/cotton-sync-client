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
    }
}
