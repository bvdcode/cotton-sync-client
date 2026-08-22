// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        [Test]
        public async Task DeleteSyncPairAsync_RemovesPair()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncApplicationService service = CreateService(store);
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(syncPair);

            await service.DeleteSyncPairAsync(syncPair.Id);

            SyncPairSettings? deleted = await store.GetAsync(syncPair.Id);
            Assert.That(deleted, Is.Null);
        }

        [Test]
        public async Task DeleteSyncPairAsync_KeepsSyncCoreStoppedAfterDeletingLastPair()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                store,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(syncPair);
            await service.StartSyncAsync();

            await service.DeleteSyncPairAsync(syncPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StartCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task DeleteSyncPairAsync_RestartsSyncComponentsWhenOtherPairsRemain()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                store,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);
            SyncPairSettings firstPair = CreatePair("/home/user/Cotton");
            SyncPairSettings secondPair = CreatePair("/home/user/Pictures");
            await service.SaveSyncPairAsync(firstPair);
            await service.SaveSyncPairAsync(secondPair);
            await service.StartSyncAsync();

            await service.DeleteSyncPairAsync(firstPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StartCallCount, Is.EqualTo(2));
                Assert.That(localChanges.StartCallCount, Is.EqualTo(2));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(2));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RestartsSyncCoreAfterLastPairDeletionWhenNewPairIsAdded()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                store,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);
            SyncPairSettings firstPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(firstPair);
            await service.StartSyncAsync();
            await service.DeleteSyncPairAsync(firstPair.Id);

            SyncPairSaveResult result = await service.SaveSyncPairAsync(CreatePair("/home/user/Pictures"));

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StartCallCount, Is.EqualTo(2));
                Assert.That(localChanges.StartCallCount, Is.EqualTo(2));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(2));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RestartsSyncCoreAfterFailedStartupAndLastPairDeletion()
        {
            List<string> calls = [];
            InvalidOperationException startupError = new("Cloud Files connect failed.");
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncCoreLifecycleComponent lifecycle = new FakeSyncCoreLifecycleComponent("cloud-files", calls)
            {
                StartException = startupError,
            };
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator(calls);
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator(calls);
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator(calls);
            SyncApplicationService service = CreateService(
                store,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync,
                syncCoreLifecycleComponents: [lifecycle]);
            SyncPairSettings firstPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(firstPair);
            InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartSyncAsync())!;
            lifecycle.StartException = null;
            await service.DeleteSyncPairAsync(firstPair.Id);

            SyncPairSaveResult result = await service.SaveSyncPairAsync(CreatePair("/home/user/Pictures"));

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.SameAs(startupError));
                Assert.That(result.IsSaved, Is.True);
                Assert.That(lifecycle.StartCallCount, Is.EqualTo(2));
                Assert.That(supervisor.StartCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task DeleteSyncPairAsync_RunsDeletionHandlerAfterStoppingSyncCore()
        {
            List<string> calls = new List<string>();
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator(calls);
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator(calls);
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator(calls);
            FakeSyncCoreLifecycleComponent lifecycle = new FakeSyncCoreLifecycleComponent("cloud-files", calls);
            FakeSyncPairDeletionHandler deletionHandler = new FakeSyncPairDeletionHandler(calls);
            SyncApplicationService service = CreateService(
                store,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync,
                syncCoreLifecycleComponents: [lifecycle],
                syncPairDeletionHandler: deletionHandler);
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(syncPair);
            await service.StartSyncAsync();
            calls.Clear();

            await service.DeleteSyncPairAsync(syncPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(deletionHandler.DeletedPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(calls.LastIndexOf("cloud-files:stop"), Is.LessThan(calls.IndexOf("deletion-handler:before-delete")));
                Assert.That(calls, Does.Not.Contain("cloud-files:start"));
            });
        }

        [Test]
        public async Task DeleteSyncPairAsync_DoesNotDeleteSettingsWhenDeletionHandlerFails()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncPairDeletionHandler deletionHandler = new FakeSyncPairDeletionHandler([])
            {
                Exception = new InvalidOperationException("Cloud Files unregister failed."),
            };
            SyncApplicationService service = CreateService(store, syncPairDeletionHandler: deletionHandler);
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(syncPair);

            InvalidOperationException? exception =
                Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteSyncPairAsync(syncPair.Id));
            SyncPairSettings? stillConfigured = await store.GetAsync(syncPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Is.EqualTo("Cloud Files unregister failed."));
                Assert.That(stillConfigured, Is.Not.Null);
            });
        }

        [Test]
        public async Task DeleteSyncPairAsync_DeletesPersistedSyncState()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncStateStore syncStateStore = new FakeSyncStateStore();
            SyncApplicationService service = CreateService(store, syncStateStore: syncStateStore);
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(syncPair);

            await service.DeleteSyncPairAsync(syncPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(syncStateStore.InitializeCallCount, Is.EqualTo(1));
                Assert.That(syncStateStore.DeletedSyncPairIds, Is.EqualTo(new[] { syncPair.Id.ToString() }));
            });
        }

        [Test]
        public async Task ListSyncPairsAsync_InitializesStore()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncApplicationService service = CreateService(store);

            await service.ListSyncPairsAsync();

            Assert.That(store.InitializeCallCount, Is.EqualTo(1));
        }
    }
}
