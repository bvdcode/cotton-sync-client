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
        public async Task LocalWatcherError_RequestsFullSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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

    }
}
