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
    }
}
