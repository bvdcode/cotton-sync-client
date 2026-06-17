// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;

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
