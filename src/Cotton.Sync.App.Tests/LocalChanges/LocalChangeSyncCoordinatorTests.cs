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
    public class LocalChangeSyncCoordinatorTests
    {
        private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(25);

        [Test]
        public async Task LocalChanges_AreCoalescedIntoOneSyncRequest()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise("/home/user/Cotton/a.txt");
            watcherFactory.CreatedWatchers[syncPair.Id].Raise("/home/user/Cotton/b.txt");

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(DebounceInterval * 3);
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastSyncNowPairId, Is.EqualTo(syncPair.Id));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "a.txt", "b.txt" }));
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalChange));
            });
        }

        [Test]
        public async Task DeletedLocalChange_RequestsScopedSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                "/home/user/Cotton/deleted.txt",
                LocalSyncRootChangeKind.Deleted);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "deleted.txt" }));
                Assert.That(supervisor.LastRequest?.LocalDeletedPaths, Is.EqualTo(new[] { "deleted.txt" }));
            });
        }

        [Test]
        public async Task RenamedLocalChange_WithOldPathRequestsScopedSyncForOldAndNewPaths()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].RaiseRename(
                "/home/user/Cotton/old.txt",
                "/home/user/Cotton/renamed.txt",
                LocalSyncRootChangeKind.Renamed);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "old.txt", "renamed.txt" }));
                Assert.That(supervisor.LastRequest?.LocalDeletedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RenamedLocalChanges_FromIgnoredTemporaryPathsRequestOnlyWorkbookTargets()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].RaiseRename(
                FullPath(syncPair, "Budget.xlsx.111111.tmp"),
                FullPath(syncPair, "Budget.xlsx"));
            watcherFactory.CreatedWatchers[syncPair.Id].RaiseRename(
                FullPath(syncPair, "Budget (1).xlsx.222222.tmp"),
                FullPath(syncPair, "Budget (1).xlsx"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(
                    supervisor.LastRequest?.LocalChangedPaths,
                    Is.EqualTo(new[] { "Budget (1).xlsx", "Budget.xlsx" }));
                Assert.That(supervisor.LastRequest?.LocalDeletedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RenamedLocalChange_ToIgnoredTemporaryPathRequestsOnlyWorkbookSource()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].RaiseRename(
                FullPath(syncPair, "Budget.xlsx"),
                FullPath(syncPair, "Budget.xlsx.111111.tmp"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Budget.xlsx" }));
                Assert.That(supervisor.LastRequest?.LocalDeletedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RenamedLocalChange_WithProviderSuppressedSourceRequestsOldAndNewPaths()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSuppression suppression = new(_ => false);
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "old.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].RaiseRename(
                FullPath(syncPair, "old.txt"),
                FullPath(syncPair, "renamed.txt"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "old.txt", "renamed.txt" }));
            });
        }

        [Test]
        public async Task RenamedLocalChange_WithProviderSuppressedSourceAndTargetDoesNotRequestSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSuppression suppression = new(_ => false);
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "old.txt");
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "renamed.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].RaiseRename(
                FullPath(syncPair, "old.txt"),
                FullPath(syncPair, "renamed.txt"));

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
        public async Task RapidCreateRenameEditDelete_CoalescesOldAndNewPathsWithFinalDeleteMarker()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            FakeWatcher watcher = watcherFactory.CreatedWatchers[syncPair.Id];
            watcher.Raise("/home/user/Cotton/old.txt", LocalSyncRootChangeKind.Created);
            watcher.Raise("/home/user/Cotton/old.txt", LocalSyncRootChangeKind.Changed);
            watcher.RaiseRename(
                "/home/user/Cotton/old.txt",
                "/home/user/Cotton/new.txt",
                LocalSyncRootChangeKind.Renamed);
            watcher.Raise("/home/user/Cotton/new.txt", LocalSyncRootChangeKind.Changed);
            watcher.Raise("/home/user/Cotton/new.txt", LocalSyncRootChangeKind.Deleted);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(DebounceInterval * 3);
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "new.txt", "old.txt" }));
                Assert.That(supervisor.LastRequest?.LocalDeletedPaths, Is.EqualTo(new[] { "new.txt" }));
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalChange));
            });
        }

        [Test]
        public async Task RenamedLocalChange_WithoutOldPathRequestsFullSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                "/home/user/Cotton/renamed.txt",
                LocalSyncRootChangeKind.Renamed);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.IsFull, Is.True);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.Empty);
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalRenameRecovery));
            });
        }

        [Test]
        public async Task LocalWatcherError_RequestsFullSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                "/home/user/Cotton",
                LocalSyncRootChangeKind.Error);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.True);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.Empty);
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalWatcherError));
            });
        }

        [Test]
        public async Task LocalChangeStorm_KeepsOnePendingSyncRequestPerPair()
        {
            const int ChangeCount = 1_000;
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.FromSeconds(5));
            await coordinator.StartAsync();

            for (int index = 0; index < ChangeCount; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise($"/home/user/Cotton/file-{index}.txt");
            }

            int pendingRequestCount = coordinator.PendingRequestCount;
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(pendingRequestCount, Is.EqualTo(1));
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
            });
        }

        [Test]
        public async Task LocalChangeOutsideRoot_DoesNotRequestFullSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise("/home/user/outside.txt");

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromMilliseconds(250));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.False);
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
            });
        }

        [Test]
        public async Task LocalChangeStorm_MaxDebounceDelayForcesSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new()
            {
                BlockSyncNow = true,
            };
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.FromMilliseconds(50),
                maxDebounceDelay: TimeSpan.FromMilliseconds(120));
            using CancellationTokenSource stormCancellation = new();
            await coordinator.StartAsync();

            Task storm = Task.Run(async () =>
            {
                int index = 0;
                while (!stormCancellation.IsCancellationRequested)
                {
                    watcherFactory.CreatedWatchers[syncPair.Id].Raise($"/home/user/Cotton/storm/file-{index}.txt");
                    index++;
                    await Task.Delay(TimeSpan.FromMilliseconds(10), stormCancellation.Token).ConfigureAwait(false);
                }
            }, stormCancellation.Token);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await stormCancellation.CancelAsync();
            await WaitForCanceledStormAsync(storm);
            supervisor.ReleaseSyncNow();
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.Not.Empty);
            });
        }

        [Test]
        public async Task LocalChangeStorm_AboveScopedLimitDoesNotKeepEveryChangedPath()
        {
            const int ChangeCount = 60_000;
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.FromSeconds(5));
            await coordinator.StartAsync();

            for (int index = 0; index < ChangeCount; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise($"/home/user/Cotton/storm/file-{index}.txt");
            }

            int pendingRequestCount = coordinator.PendingRequestCount;
            int pendingChangedPathCount = coordinator.PendingChangedPathCount;
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(pendingRequestCount, Is.EqualTo(1));
                Assert.That(pendingChangedPathCount, Is.Zero);
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
            });
        }

        [Test]
        public async Task LocalChangeStorm_AboveScopedLimitRequestsOneFullSync()
        {
            int changeCount = PendingLocalSyncRequest.MaxScopedChangedPaths + 2_000;
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            for (int index = 0; index < changeCount; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise($"/home/user/Cotton/storm/file-{index}.txt");
            }

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.True);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.Empty);
                Assert.That(
                    supervisor.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.LocalChangeOverflow));
            });
        }

        [Test]
        public async Task WindowsVirtualFilesLocalChangeStorm_AboveDefaultScopedLimitRequestsScopedSync()
        {
            int changeCount = PendingLocalSyncRequest.MaxScopedChangedPaths + 2_000;
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            for (int index = 0; index < changeCount; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise($"/home/user/Cotton/storm/file-{index}.txt");
            }

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Has.Count.EqualTo(changeCount));
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Does.Contain("storm/file-0.txt"));
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Does.Contain($"storm/file-{changeCount - 1}.txt"));
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalChange));
            });
        }

        [Test]
        public async Task WindowsVirtualFilesLocalChangeStorm_AboveVfsLimitRequestsFullSync()
        {
            int changeCount = PendingLocalSyncRequest.MaxWindowsVirtualFilesScopedChangedPaths + 100;
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);
            await coordinator.StartAsync();

            for (int index = 0; index < changeCount; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise($"/home/user/Cotton/storm/file-{index}.txt");
            }

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.IsFull, Is.True);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.Empty);
                Assert.That(
                    supervisor.LastRequest?.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.LocalChangeOverflow));
            });
        }

        [Test]
        public async Task WindowsVirtualFilesRootChange_RequestsScopedRootPath()
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

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(syncPair.LocalRootPath);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "." }));
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalChange));
            });
        }

        [Test]
        public async Task ProviderSuppressedFileChange_DoesNotRequestSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var coordinator = new LocalChangeSyncCoordinator(
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
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var coordinator = new LocalChangeSyncCoordinator(
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
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var coordinator = new LocalChangeSyncCoordinator(
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
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var coordinator = new LocalChangeSyncCoordinator(
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
                suppression.SuppressProviderFileCreation(syncPair.Id, rootPath, relativePath);
                AtomicLocalFileSyncWriter writer = new();
                await writer.WriteFileAsync(
                    rootPath,
                    relativePath,
                    async (stream, cancellationToken) =>
                        await stream.WriteAsync("remote-content"u8.ToArray(), cancellationToken),
                    new DateTime(2026, 8, 3, 20, 0, 0, DateTimeKind.Utc));

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

        [Test]
        public async Task ProviderSuppressedChangeStorm_LogsOneProviderOriginSummaryWithoutPerFileFlood()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var logger = new RecordingLogger<LocalChangeSyncCoordinator>();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                logger,
                suppression);
            for (int index = 0; index < 250; index++)
            {
                suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, $"Cloud/generated-{index}.txt");
            }

            await coordinator.StartAsync();
            for (int index = 0; index < 250; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                    FullPath(syncPair, $"Cloud/generated-{index}.txt"),
                    LocalSyncRootChangeKind.Created);
            }

            bool observed = await supervisor.WaitForSyncAsync(DebounceInterval * 4);
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.False);
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
                Assert.That(logger.Entries, Has.Count.EqualTo(1));
                Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Information));
                Assert.That(logger.Entries[0].Message, Does.Contain("origin provider"));
                Assert.That(logger.Entries[0].Message, Does.Contain("subsequent provider echoes are coalesced"));
            });
        }

        [Test]
        public async Task UserChange_LogsUserOrExternalOriginBeforeRequestingSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var logger = new RecordingLogger<LocalChangeSyncCoordinator>();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                logger);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "Cloud/user-edit.txt"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(logger.Entries, Has.Count.EqualTo(1));
                Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Information));
                Assert.That(logger.Entries[0].Message, Does.Contain("origin user-or-external"));
                Assert.That(logger.Entries[0].Message, Does.Contain("Cloud" + Path.DirectorySeparatorChar + "user-edit.txt"));
            });
        }

        [Test]
        public async Task ProviderWriteBurstWatcherOverflow_DoesNotRequestFullSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                syncPair.LocalRootPath,
                LocalSyncRootChangeKind.Error);

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
        public async Task ProviderWriteBurstLateWatcherOverflow_DoesNotRequestFullSyncDuringGrace()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            using (suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
            }

            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                syncPair.LocalRootPath,
                LocalSyncRootChangeKind.Error);

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
        public async Task ProviderWriteBurstExpiredGrace_DoesNotHideRealWatcherError()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var timeProvider = new MutableTimeProvider();
            var suppression = new LocalChangeSuppression(
                entryLifetime: TimeSpan.FromSeconds(1),
                timeProvider: timeProvider);
            using (suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
            }

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                syncPair.LocalRootPath,
                LocalSyncRootChangeKind.Error);

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.True);
            });
        }

        [Test]
        public async Task ProviderWriteBurst_DoesNotHideNormalUserChange()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "Cloud/user-edit.txt"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Cloud/user-edit.txt" }));
            });
        }

        [Test]
        public async Task ProviderWriteBurst_OnlineOnlyRegistrationDoesNotHidePinnedUserChange()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression(_ => false);
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            suppression.SuppressProviderOnlineOnlyWrite(syncPair.Id, syncPair.LocalRootPath, "Music");
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "Music"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Music" }));
            });
        }

        [Test]
        public async Task ProviderWriteBurst_RegisteredPathStormDoesNotExhaustEventBudget()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            LocalChangeSuppression suppression = new(
                _ => false,
                eventBudget: 2,
                maxEntriesPerPair: 4);
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            for (int index = 0; index < 20; index++)
            {
                suppression.SuppressProviderWrite(
                    syncPair.Id,
                    syncPair.LocalRootPath,
                    $"Cloud/generated-{index}.txt");
            }

            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            for (int index = 0; index < 20; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                    FullPath(syncPair, $"Cloud/generated-{index}.txt"),
                    LocalSyncRootChangeKind.Changed);
            }

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
        public void ProviderWriteBurst_RegisteredPathGraceStartsWhenBurstEnds()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            MutableTimeProvider timeProvider = new();
            LocalChangeSuppression suppression = new(
                _ => false,
                entryLifetime: TimeSpan.FromSeconds(1),
                eventBudget: 2,
                timeProvider: timeProvider);
            IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "Cloud/hydrated.txt");
            LocalSyncRootChange change = new(
                syncPair.Id,
                FullPath(syncPair, "Cloud/hydrated.txt"),
                LocalSyncRootChangeKind.Changed);

            timeProvider.Advance(TimeSpan.FromSeconds(10));
            bool suppressedDuringLongBurst = suppression.ShouldSuppress(change);
            burst.Dispose();
            timeProvider.Advance(TimeSpan.FromMilliseconds(500));
            bool suppressedDuringGrace = suppression.ShouldSuppress(change);
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            bool suppressedAfterGrace = suppression.ShouldSuppress(change);

            Assert.Multiple(() =>
            {
                Assert.That(suppressedDuringLongBurst, Is.True);
                Assert.That(suppressedDuringGrace, Is.True);
                Assert.That(suppressedAfterGrace, Is.False);
            });
        }

        [Test]
        public void ProviderWriteBurstGrace_RecognizesRecallOnlyCloudFilesAttributes()
        {
            FileAttributes recallOnOpen = FileAttributes.Archive | (FileAttributes)0x00040000;
            FileAttributes recallOnDataAccess = FileAttributes.Archive | (FileAttributes)0x00400000;
            FileAttributes pinnedOffline = FileAttributes.Offline | (FileAttributes)0x00080000;

            Assert.Multiple(() =>
            {
                Assert.That(LocalChangeSuppression.IsOnlineOnlyCloudFilesAttributes(recallOnOpen), Is.True);
                Assert.That(LocalChangeSuppression.IsOnlineOnlyCloudFilesAttributes(recallOnDataAccess), Is.True);
                Assert.That(LocalChangeSuppression.IsOnlineOnlyCloudFilesAttributes(FileAttributes.Offline), Is.True);
                Assert.That(LocalChangeSuppression.IsOnlineOnlyCloudFilesAttributes(pinnedOffline), Is.False);
                Assert.That(LocalChangeSuppression.IsOnlineOnlyCloudFilesAttributes(FileAttributes.ReparsePoint), Is.False);
            });
        }

        [Test]
        public async Task ProviderWriteBurstLatePlaceholderStorm_DoesNotRequestSyncAfterEntryCapacityTrim()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression(
                path => path.Contains("generated-", StringComparison.OrdinalIgnoreCase),
                maxEntriesPerPair: 4);
            using (IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
                for (int index = 0; index < 20; index++)
                {
                    suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, $"Cloud/generated-{index}.txt");
                }
            }

            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            for (int index = 0; index < 20; index++)
            {
                watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, $"Cloud/generated-{index}.txt"));
            }

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
        public async Task ProviderWriteBurstGrace_DoesNotHideNormalUserChange()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression(
                path => path.Contains("generated-", StringComparison.OrdinalIgnoreCase));
            using (suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
            }

            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "Cloud/user-edit.txt"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Cloud/user-edit.txt" }));
            });
        }

        [Test]
        public async Task ProviderSuppressedParentDirectoryChange_DoesNotRequestSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "Cloud/Nested/remote-only.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(
                FullPath(syncPair, "Cloud/Nested"),
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
        public async Task ProviderSuppression_DoesNotHideSiblingUserChange()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var suppression = new LocalChangeSuppression();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                changeSuppression: suppression);
            suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, "Cloud/remote-only.txt");
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "Cloud/user-edit.txt"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest?.IsFull, Is.False);
                Assert.That(supervisor.LastRequest?.LocalChangedPaths, Is.EqualTo(new[] { "Cloud/user-edit.txt" }));
            });
        }

        [Test]
        public async Task StartAsync_DoesNotWatchDisabledPairs()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: false);
            var watcherFactory = new FakeWatcherFactory();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                new FakeSyncSupervisor(),
                watcherFactory,
                DebounceInterval);

            await coordinator.StartAsync();
            await coordinator.StopAsync();

            Assert.That(watcherFactory.CreatedWatchers, Is.Empty);
        }

        [Test]
        public async Task StartAsync_RequestsDetectedOfflineChangesAfterWatcherStarts()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            SyncRunRequest expectedRequest = SyncRunRequest.ForLocalChangedPaths(
                ["Docs/old.txt", "Docs/renamed.txt"],
                ["Docs/old.txt"],
                SyncRunCause.LocalChange);
            var detector = new FakeOfflineChangeDetector(expectedRequest);
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                offlineChangeDetector: detector);

            await coordinator.StartAsync();
            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(detector.DetectedPairs, Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(watcherFactory.CreatedWatchers[syncPair.Id].StartCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastRequest, Is.SameAs(expectedRequest));
            });
        }

        [Test]
        public async Task StartAsync_OfflineDetectionFailureRequestsFullRecovery()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var detector = new FakeOfflineChangeDetector(new InvalidOperationException("Scan failed."));
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                offlineChangeDetector: detector);

            await coordinator.StartAsync();
            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.LastRequest?.IsFull, Is.True);
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalWatcherError));
            });
        }

        [Test]
        public async Task StopAsync_CancelsPendingSyncRequest()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.FromMilliseconds(100));
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise("/home/user/Cotton/a.txt");
            await coordinator.StopAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            Assert.That(supervisor.SyncNowCallCount, Is.Zero);
        }

        [Test]
        public async Task StopAsync_WaitsForRunningSyncRequest()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory();
            var supervisor = new FakeSyncSupervisor
            {
                BlockSyncNow = true,
            };
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.Zero);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise("/home/user/Cotton/a.txt");
            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            Task stopTask = coordinator.StopAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(75));

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(stopTask.IsCompleted, Is.False);
            });

            supervisor.ReleaseSyncNow();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task StartAsync_CleansCreatedWatchersWhenLaterWatcherFails()
        {
            SyncPairSettings firstPair = CreatePair(isEnabled: true);
            SyncPairSettings secondPair = CreatePair(isEnabled: true);
            var watcherFactory = new FakeWatcherFactory
            {
                FailingStartPairId = secondPair.Id,
            };
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new LocalChangeSyncCoordinator(
                new FakeSyncPairSettingsStore([firstPair, secondPair]),
                supervisor,
                watcherFactory,
                DebounceInterval);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await coordinator.StartAsync());

            watcherFactory.CreatedWatchers[firstPair.Id].Raise("/home/user/Cotton/a.txt");
            await Task.Delay(DebounceInterval * 3);

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Is.EqualTo("Watcher failed to start."));
                Assert.That(watcherFactory.CreatedWatchers[firstPair.Id].StopCallCount, Is.EqualTo(1));
                Assert.That(watcherFactory.CreatedWatchers[firstPair.Id].DisposeAsyncCallCount, Is.EqualTo(1));
                Assert.That(watcherFactory.CreatedWatchers[secondPair.Id].StopCallCount, Is.EqualTo(1));
                Assert.That(watcherFactory.CreatedWatchers[secondPair.Id].DisposeAsyncCallCount, Is.EqualTo(1));
                Assert.That(supervisor.SyncNowCallCount, Is.Zero);
            });
        }

        private static SyncPairSettings CreatePair(bool isEnabled, SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = mode,
            };
        }

        private static string FullPath(SyncPairSettings syncPair, string relativePath)
        {
            return Path.Combine(
                syncPair.LocalRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static async Task WaitForCanceledStormAsync(Task storm)
        {
            try
            {
                await storm.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class MutableTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow()
            {
                return _utcNow;
            }

            public void Advance(TimeSpan duration)
            {
                _utcNow = _utcNow.Add(duration);
            }
        }

        private sealed class BlockingLocalChangeSuppression : ILocalChangeSuppression
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
            }

            public void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath)
            {
            }

            public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
            {
                return NoopDisposable.Instance;
            }

            public bool ShouldSuppress(LocalSyncRootChange change)
            {
                Entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
                return false;
            }

            public void Release()
            {
                _release.TrySetResult();
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private class FakeWatcherFactory : ILocalSyncRootWatcherFactory
        {
            public Dictionary<Guid, FakeWatcher> CreatedWatchers { get; } = [];

            public Guid? FailingStartPairId { get; set; }

            public ILocalSyncRootWatcher Create(SyncPairSettings syncPair)
            {
                var watcher = new FakeWatcher(syncPair.Id);
                if (syncPair.Id == FailingStartPairId)
                {
                    watcher.StartException = new InvalidOperationException("Watcher failed to start.");
                }

                CreatedWatchers.Add(syncPair.Id, watcher);
                return watcher;
            }
        }

        private class FakeWatcher : ILocalSyncRootWatcher
        {
            private readonly Guid _syncPairId;

            public FakeWatcher(Guid syncPairId)
            {
                _syncPairId = syncPairId;
            }

            public event EventHandler<LocalSyncRootChange>? Changed;

            public Exception? StartException { get; set; }

            public int DisposeAsyncCallCount { get; private set; }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCallCount++;
                return ValueTask.CompletedTask;
            }

            public void Raise(string fullPath, LocalSyncRootChangeKind kind = LocalSyncRootChangeKind.Changed)
            {
                Changed?.Invoke(this, new LocalSyncRootChange(
                    _syncPairId,
                    fullPath,
                    kind));
            }

            public void RaiseRename(string oldFullPath, string fullPath, LocalSyncRootChangeKind kind = LocalSyncRootChangeKind.Renamed)
            {
                Changed?.Invoke(this, new LocalSyncRootChange(
                    _syncPairId,
                    fullPath,
                    kind,
                    oldFullPath));
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartCallCount++;
                if (StartException is not null)
                {
                    throw StartException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StopCallCount++;
                return Task.CompletedTask;
            }
        }

        private class FakeOfflineChangeDetector : ILocalOfflineChangeDetector
        {
            private readonly Exception? _exception;
            private readonly SyncRunRequest? _request;

            public FakeOfflineChangeDetector(SyncRunRequest? request)
            {
                _request = request;
            }

            public FakeOfflineChangeDetector(Exception exception)
            {
                _exception = exception;
            }

            public List<Guid> DetectedPairs { get; } = [];

            public Task<SyncRunRequest?> DetectAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DetectedPairs.Add(syncPair.Id);
                return _exception is null
                    ? Task.FromResult(_request)
                    : Task.FromException<SyncRunRequest?>(_exception);
            }
        }

        private class FakeSyncPairSettingsStore : ISyncPairSettingsStore
        {
            private readonly IReadOnlyList<SyncPairSettings> _syncPairs;

            public FakeSyncPairSettingsStore(IReadOnlyList<SyncPairSettings> syncPairs)
            {
                _syncPairs = syncPairs;
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_syncPairs.SingleOrDefault(pair => pair.Id == syncPairId));
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_syncPairs);
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class FakeSyncSupervisor : ISyncSupervisor
        {
            private readonly TaskCompletionSource _syncRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseSyncNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public bool BlockSyncNow { get; set; }

            public int SyncNowCallCount { get; private set; }

            public Guid? LastSyncNowPairId { get; private set; }

            public SyncRunRequest? LastRequest { get; private set; }

            public Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(bool startPaused, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return SyncAllAsync(cancellationToken);
            }

            public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return SyncNowAsync(syncPairId, SyncRunRequest.Full, cancellationToken);
            }

            public Task SyncNowAsync(Guid syncPairId, SyncRunRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncNowCallCount++;
                LastSyncNowPairId = syncPairId;
                LastRequest = request;
                _syncRequested.TrySetResult();
                return BlockSyncNow
                    ? _releaseSyncNow.Task
                    : Task.CompletedTask;
            }

            public async Task<bool> WaitForSyncAsync(TimeSpan timeout)
            {
                Task completed = await Task.WhenAny(_syncRequested.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == _syncRequested.Task;
            }

            public void ReleaseSyncNow()
            {
                _releaseSyncNow.TrySetResult();
            }
        }

        private class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
