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
        public async Task ProviderSuppressedChangeStorm_LogsOneProviderOriginSummaryWithoutPerFileFlood()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            RecordingLogger<LocalChangeSyncCoordinator> logger = new RecordingLogger<LocalChangeSyncCoordinator>();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            RecordingLogger<LocalChangeSyncCoordinator> logger = new RecordingLogger<LocalChangeSyncCoordinator>();
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
        public async Task ProviderWriteBurstWatcherOverflow_RequestsFullSync()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalWatcherError));
            });
        }

        [Test]
        public async Task ProviderWriteBurstLateWatcherOverflow_RequestsFullSyncDuringGrace()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            using (suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
            }

            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
                Assert.That(supervisor.LastRequest?.Causes, Is.EqualTo(SyncRunCause.LocalWatcherError));
            });
        }

        [Test]
        public async Task ProviderWriteBurstExpiredGrace_DoesNotHideRealWatcherError()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            MutableTimeProvider timeProvider = new MutableTimeProvider();
            LocalChangeSuppression suppression = new LocalChangeSuppression(
                entryLifetime: TimeSpan.FromSeconds(1),
                timeProvider: timeProvider);
            using (suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
            }

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression();
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression(
                _ => false,
                pinnedCloudFilesPlaceholderProbe: _ => true);
            using IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            suppression.SuppressProviderOnlineOnlyWrite(syncPair.Id, syncPair.LocalRootPath, "Music");
            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
        public void ProviderWriteBurst_RegisteredPathsRespectCapacityAndPostBurstEventBudget()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true, SyncPairMode.WindowsVirtualFiles);
            LocalChangeSuppression suppression = new(
                _ => false,
                eventBudget: 2,
                maxEntriesPerPair: 4);
            IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            for (int index = 0; index < 5; index++)
            {
                suppression.SuppressProviderWrite(
                    syncPair.Id,
                    syncPair.LocalRootPath,
                    $"Cloud/generated-{index}.txt");
            }

            LocalSyncRootChange evictedChange = new(
                syncPair.Id,
                FullPath(syncPair, "Cloud/generated-0.txt"),
                LocalSyncRootChangeKind.Changed);
            LocalSyncRootChange retainedChange = new(
                syncPair.Id,
                FullPath(syncPair, "Cloud/generated-4.txt"),
                LocalSyncRootChangeKind.Changed);

            Assert.Multiple(() =>
            {
                Assert.That(suppression.ShouldSuppress(evictedChange), Is.False);
                Assert.That(suppression.ShouldSuppress(retainedChange), Is.True);
                Assert.That(suppression.ShouldSuppress(retainedChange), Is.True);
            });

            burst.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(suppression.ShouldSuppress(retainedChange), Is.True);
                Assert.That(suppression.ShouldSuppress(retainedChange), Is.True);
                Assert.That(suppression.ShouldSuppress(retainedChange), Is.False);
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

            bool suppressedDuringBurst = suppression.ShouldSuppress(change);
            burst.Dispose();
            timeProvider.Advance(TimeSpan.FromMilliseconds(500));
            bool suppressedDuringGrace = suppression.ShouldSuppress(change);
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            bool suppressedAfterGrace = suppression.ShouldSuppress(change);

            Assert.Multiple(() =>
            {
                Assert.That(suppressedDuringBurst, Is.True);
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression(
                path => path.Contains("generated-", StringComparison.OrdinalIgnoreCase),
                maxEntriesPerPair: 4);
            using (IDisposable burst = suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
                for (int index = 0; index < 20; index++)
                {
                    suppression.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, $"Cloud/generated-{index}.txt");
                }
            }

            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
            FakeWatcherFactory watcherFactory = new FakeWatcherFactory();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            LocalChangeSuppression suppression = new LocalChangeSuppression(
                path => path.Contains("generated-", StringComparison.OrdinalIgnoreCase));
            using (suppression.SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath))
            {
            }

            LocalChangeSyncCoordinator coordinator = new LocalChangeSyncCoordinator(
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
    }
}
