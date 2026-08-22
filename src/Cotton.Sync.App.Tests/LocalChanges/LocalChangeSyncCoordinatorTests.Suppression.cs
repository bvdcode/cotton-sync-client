// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public partial class LocalChangeSyncCoordinatorTests
    {
        [Test]
        public async Task ProviderSuppressedFileChange_DoesNotRequestSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "Cloud/remote-only.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Cloud/remote-only.txt"),
                LocalSyncRootChangeKind.Created);

            bool observed = await supervisor.WaitForSyncAsync(DebounceInterval * 4);
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.False);
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
            });
        }

        [Test]
        public async Task ProviderMetadataSuppression_DoesNotHideCrossDirectoryMoveDelete()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderMetadataWrite(
                syncPair.Id,
                syncPair.LocalRootPath,
                "Source/move.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Source/move.txt"),
                LocalSyncRootChangeKind.Deleted);
            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Target/moved.txt"),
                LocalSyncRootChangeKind.Created);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(
                    supervisor.LastRequest?.LocalChangedPaths,
                    Is.EquivalentTo(new[] { "Source/move.txt", "Target/moved.txt" }));
                Assert.That(
                    supervisor.LastRequest?.LocalDeletedPaths,
                    Is.EqualTo(new[] { "Source/move.txt" }));
            });
        }

        [Test]
        public async Task ProviderMetadataSuppression_StillSuppressesFinalizationChange()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderMetadataWrite(
                syncPair.Id,
                syncPair.LocalRootPath,
                "Cloud/finalized.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Cloud/finalized.txt"),
                LocalSyncRootChangeKind.AttributesChanged);

            bool observed = await supervisor.WaitForSyncAsync(DebounceInterval * 4);
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.False);
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
            });
        }

        [Test]
        public async Task ProviderMetadataSuppression_DoesNotHideSubsequentContentEdit()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderMetadataWrite(
                syncPair.Id,
                syncPair.LocalRootPath,
                "Cloud/finalized.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Cloud/finalized.txt"),
                LocalSyncRootChangeKind.Changed);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(
                    supervisor.LastRequest?.LocalChangedPaths,
                    Is.EqualTo(new[] { "Cloud/finalized.txt" }));
            });
        }

        [Test]
        public async Task ProviderFileCreationSuppression_DoesNotHideSubsequentContentEdit()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSuppression suppression = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderFileCreation(
                syncPair.Id,
                syncPair.LocalRootPath,
                "Docs/file (Cotton conflict 20260803T200000Z).txt");
            await coordinator.StartAsync();

            string fullPath = FullPath(syncPair, "Docs/file (Cotton conflict 20260803T200000Z).txt");
            watcherFactory.CreatedWatchers[syncPair.Id].Raise(fullPath, LocalSyncRootChangeKind.Created);
            await Task.Delay(DebounceInterval * 4);
            watcherFactory.CreatedWatchers[syncPair.Id].Raise(fullPath, LocalSyncRootChangeKind.Changed);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(
                    supervisor.LastRequest?.LocalChangedPaths,
                    Is.EqualTo(new[] { "Docs/file (Cotton conflict 20260803T200000Z).txt" }));
            });
        }

        [Test]
        public void ProviderFileMaterializationSuppression_StopsAfterEventBudgetOrFileChange()
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "cotton-provider-file-materialization",
                Guid.NewGuid().ToString("N"));
            const string relativePath = "Docs/restored.txt";
            string fullPath = Path.Combine(rootPath, "Docs", "restored.txt");
            Guid syncPairId = Guid.NewGuid();
            DateTime expectedLastWriteUtc = new(2026, 8, 3, 20, 0, 0, DateTimeKind.Utc);
            byte[] content = "remote-content"u8.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);
            File.SetLastWriteTimeUtc(fullPath, expectedLastWriteUtc);
            LocalChangeSuppression suppression = new LocalChangeSuppression(eventBudget: 2);
            suppression.SuppressProviderFileMaterialization(
                syncPairId,
                rootPath,
                relativePath,
                content.Length,
                expectedLastWriteUtc);

            try
            {
                for (int index = 0; index < 2; index++)
                {
                    Assert.That(
                        suppression.ShouldSuppress(new LocalSyncRootChange(
                            syncPairId,
                            fullPath,
                            index == 0 ? LocalSyncRootChangeKind.Created : LocalSyncRootChangeKind.Changed)),
                        Is.True);
                }

                Assert.That(
                    suppression.ShouldSuppress(new LocalSyncRootChange(
                        syncPairId,
                        fullPath,
                        LocalSyncRootChangeKind.Changed)),
                    Is.False);

                suppression.SuppressProviderFileMaterialization(
                    syncPairId,
                    rootPath,
                    relativePath,
                    content.Length,
                    expectedLastWriteUtc);
                File.AppendAllText(fullPath, "-user-edit");

                Assert.That(
                    suppression.ShouldSuppress(new LocalSyncRootChange(
                        syncPairId,
                        fullPath,
                        LocalSyncRootChangeKind.Changed)),
                    Is.False);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }

        [Test]
        public void ProviderFileMaterializationSuppression_StopsAfterExpiry()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), "cotton-provider-file-materialization-expiry");
            const string relativePath = "Docs/restored.txt";
            string fullPath = Path.Combine(rootPath, "Docs", "restored.txt");
            Guid syncPairId = Guid.NewGuid();
            DateTime expectedLastWriteUtc = new(2026, 8, 3, 20, 0, 0, DateTimeKind.Utc);
            byte[] content = "remote-content"u8.ToArray();
            MutableTimeProvider timeProvider = new();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);
            File.SetLastWriteTimeUtc(fullPath, expectedLastWriteUtc);
            LocalChangeSuppression suppression = new(
                entryLifetime: TimeSpan.FromSeconds(1),
                timeProvider: timeProvider);
            suppression.SuppressProviderFileMaterialization(
                syncPairId,
                rootPath,
                relativePath,
                content.Length,
                expectedLastWriteUtc);

            try
            {
                timeProvider.Advance(TimeSpan.FromSeconds(2));

                Assert.That(
                    suppression.ShouldSuppress(new LocalSyncRootChange(
                        syncPairId,
                        fullPath,
                        LocalSyncRootChangeKind.Changed)),
                    Is.False);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }

        [Test]
        public async Task UserCreatedConflictLookalikeWithoutSuppression_RequestsSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Docs/user (Cotton conflict 20260803T200000Z).txt"),
                LocalSyncRootChangeKind.Created);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(
                    supervisor.LastRequest?.LocalChangedPaths,
                    Is.EqualTo(new[] { "Docs/user (Cotton conflict 20260803T200000Z).txt" }));
            });
        }

        [Test]
        public async Task ProviderFileCreationSuppression_WithAtomicWriterSuppressesEchoButNotUserEdit()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Ignore("Windows FileSystemWatcher move semantics are required for this test.");
            }

            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "cotton-provider-file-creation",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(rootPath, "Docs"));
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            syncPair.LocalRootPath = rootPath;
            FakeSyncSupervisor supervisor = new();
            LocalChangeSuppression suppression = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                new FileSystemLocalSyncRootWatcherFactory(),
                DebounceInterval,
                changeSuppression: suppression);
            const string relativePath = "Docs/file (Cotton conflict 20260803T200000Z).txt";
            string fullPath = FullPath(syncPair, relativePath);

            await coordinator.StartAsync();
            try
            {
                const string remoteContent = "remote-content";
                DateTime expectedLastWriteUtc = new(2026, 8, 3, 20, 0, 0, DateTimeKind.Utc);
                suppression.SuppressProviderFileMaterialization(
                    syncPair.Id,
                    rootPath,
                    relativePath,
                    remoteContent.Length,
                    expectedLastWriteUtc);
                AtomicLocalFileSyncWriter writer = new();
                await writer.WriteFileAsync(
                    rootPath,
                    relativePath,
                    async (stream, cancellationToken) =>
                        await stream.WriteAsync("remote-content"u8.ToArray(), cancellationToken),
                    expectedLastWriteUtc);

                await Task.Delay(TimeSpan.FromMilliseconds(500));
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);

                await File.AppendAllTextAsync(fullPath, "-user-edit");
                bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(5));

                Assert.Multiple(() =>
                {
                    Assert.That(observed, Is.True);
                    Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                    Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                    Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { relativePath }));
                });
            }
            finally
            {
                await coordinator.StopAsync();
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }

        [Test]
        public async Task StopAsync_DuringSuppressionDoesNotRaceLifetimeDispose()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            BlockingLocalChangeSuppression suppression = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            Task raiseTask = Task.Run(() => watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                "/home/user/Cotton/user-edit.txt"));
            await suppression.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await coordinator.StopAsync();
            suppression.Release();
            await raiseTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(supervisor.SyncNowCallCount, Is.Zero);
        }
    }
}
