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
        public async Task LocalChanges_AreCoalescedIntoOneSyncRequest()
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
        public async Task TransientConnectionFailure_RetriesScopedRequestUntilItSucceeds()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            supervisor.SyncNowExceptions.Enqueue(new HttpRequestException("Network unavailable."));
            RecordingLogger<LocalChangeSyncCoordinator> logger = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.Zero,
                logger,
                connectionRetryInterval: TimeSpan.FromMilliseconds(1),
                delayAsync: static (_, _) => Task.CompletedTask);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "offline-edit.txt"));

            bool retried = await supervisor.WaitForSyncCallCountAsync(2, TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(retried, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(2));
                Assert.That(supervisor.Requests, Has.Count.EqualTo(2));
                Assert.That(
                    supervisor.Requests.Select(static request => request.LocalChangedPaths),
                    Is.All.EqualTo(new[] { "offline-edit.txt" }));
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
                Assert.That(
                    logger.Entries.Any(entry => entry.Level == LogLevel.Warning
                        && entry.Message.Contains("retrying after", StringComparison.Ordinal)),
                    Is.True);
                Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Error), Is.False);
            });
        }

        [Test]
        public async Task LocalChanges_DuringConnectionOutageShareOneRecoveryLoop()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            supervisor.SyncNowExceptions.Enqueue(new HttpRequestException("Network unavailable."));
            TaskCompletionSource retryDelayStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.Zero,
                connectionRetryInterval: TimeSpan.FromSeconds(15),
                delayAsync: async (_, cancellationToken) =>
                {
                    retryDelayStarted.TrySetResult();
                    await releaseRetry.Task.WaitAsync(cancellationToken);
                });
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "first.txt"));
            await retryDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "second.txt"));
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(coordinator.PendingRequestCount, Is.EqualTo(1));
            });

            releaseRetry.TrySetResult();
            bool completedFollowUp = await supervisor.WaitForSyncCallCountAsync(3, TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(completedFollowUp, Is.True);
                Assert.That(supervisor.Requests, Has.Count.EqualTo(3));
                Assert.That(supervisor.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "first.txt" }));
                Assert.That(supervisor.Requests[1].LocalChangedPaths, Is.EqualTo(new[] { "first.txt" }));
                Assert.That(
                    supervisor.Requests[2].LocalChangedPaths,
                    Is.EqualTo(new[] { "first.txt", "second.txt" }));
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
            });
        }

        [Test]
        public async Task NonTransientFailure_DoesNotRetryScopedRequest()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            supervisor.SyncNowExceptions.Enqueue(new InvalidOperationException("Permanent failure."));
            RecordingLogger<LocalChangeSyncCoordinator> logger = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.Zero,
                logger,
                connectionRetryInterval: TimeSpan.FromMilliseconds(1),
                delayAsync: static (_, _) => Task.CompletedTask);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "invalid.txt"));

            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
                Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Warning), Is.False);
                Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Error), Is.True);
            });
        }

        [Test]
        public async Task StopAsync_CancelsConnectionRecoveryDelayWithoutAnotherAttempt()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            supervisor.SyncNowExceptions.Enqueue(new HttpRequestException("Network unavailable."));
            TaskCompletionSource retryDelayStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.Zero,
                connectionRetryInterval: TimeSpan.FromSeconds(15),
                delayAsync: async (_, cancellationToken) =>
                {
                    retryDelayStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "offline-edit.txt"));
            await retryDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
            });
        }

        [Test]
        public async Task RenamedLocalChange_WithOldPathRequestsScopedSyncForOldAndNewPaths()
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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

    }
}
