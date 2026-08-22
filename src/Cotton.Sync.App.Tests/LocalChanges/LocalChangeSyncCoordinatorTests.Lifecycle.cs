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
        public async Task StartAsync_DoesNotWatchDisabledPairs()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: false);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            SyncRunRequest expectedRequest = SyncRunRequest.ForLocalChangedPaths(
                ["Docs/old.txt", "Docs/renamed.txt"],
                ["Docs/old.txt"],
                SyncRunCause.LocalChange);
            FakeOfflineChangeDetector detector = new FakeOfflineChangeDetector(expectedRequest);
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
        public async Task StartAsync_TransientOfflineReconciliationFailureRetriesUntilItSucceeds()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            supervisor.SyncNowExceptions.Enqueue(new HttpRequestException("Network unavailable."));
            SyncRunRequest expectedRequest = SyncRunRequest.ForLocalChangedPaths(
                ["Docs/offline.txt"],
                causes: SyncRunCause.LocalChange);
            FakeOfflineChangeDetector detector = new(expectedRequest);
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                DebounceInterval,
                offlineChangeDetector: detector,
                connectionRetryInterval: TimeSpan.FromMilliseconds(1),
                delayAsync: static (_, _) => Task.CompletedTask);

            await coordinator.StartAsync();
            bool retried = await supervisor.WaitForSyncCallCountAsync(2, TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(retried, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(2));
                Assert.That(supervisor.Requests, Is.All.SameAs(expectedRequest));
            });
        }

        [Test]
        public async Task StartAsync_OfflineDetectionFailureRequestsFullRecovery()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeOfflineChangeDetector detector = new FakeOfflineChangeDetector(new InvalidOperationException("Scan failed."));
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor
            {
                BlockSyncNow = true,
            };
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory
            {
                FailingStartPairId = secondPair.Id,
            };
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
    }
}
