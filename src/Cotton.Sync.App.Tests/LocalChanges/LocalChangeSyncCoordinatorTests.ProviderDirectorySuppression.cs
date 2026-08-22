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
        public async Task ProviderSuppressedParentDirectoryChange_DoesNotRequestSync()
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
