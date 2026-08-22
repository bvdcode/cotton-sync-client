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
        public async Task SaveSyncPairAsync_RejectsLocalRootChangeWithoutDeletingSyncState()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncStateStore syncStateStore = new FakeSyncStateStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await store.UpsertAsync(syncPair);
            SyncApplicationService service = CreateService(store, syncStateStore: syncStateStore);
            SyncPairSettings moved = CopySyncPair(syncPair);
            moved.LocalRootPath = "/home/user/Cotton Documents";
            moved.UpdatedAtUtc = DateTime.UtcNow;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(moved);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(result.Validation.Errors.Select(error => error.Issue), Is.EqualTo(new[]
                {
                    SyncPairValidationIssue.SyncScopeChangeNotSupported,
                }));
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.LocalRootPath, Is.EqualTo("/home/user/Cotton"));
                Assert.That(syncStateStore.InitializeCallCount, Is.Zero);
                Assert.That(syncStateStore.DeletedSyncPairIds, Is.Empty);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RejectsRemoteRootChangeWithoutDeletingSyncState()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncStateStore syncStateStore = new FakeSyncStateStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await store.UpsertAsync(syncPair);
            SyncApplicationService service = CreateService(store, syncStateStore: syncStateStore);
            SyncPairSettings moved = CopySyncPair(syncPair);
            moved.RemoteRootNodeId = Guid.NewGuid();
            moved.RemoteDisplayPath = "/Documents Archive";
            moved.UpdatedAtUtc = DateTime.UtcNow;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(moved);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(result.Validation.Errors.Select(error => error.Issue), Is.EqualTo(new[]
                {
                    SyncPairValidationIssue.SyncScopeChangeNotSupported,
                }));
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.RemoteRootNodeId, Is.EqualTo(syncPair.RemoteRootNodeId));
                Assert.That(saved.RemoteDisplayPath, Is.EqualTo("/Documents"));
                Assert.That(syncStateStore.InitializeCallCount, Is.Zero);
                Assert.That(syncStateStore.DeletedSyncPairIds, Is.Empty);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RejectsSyncModeChangeWithoutDeletingSyncState()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncStateStore syncStateStore = new FakeSyncStateStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await store.UpsertAsync(syncPair);
            SyncPairSettingsValidator validator = new(new SyncPairModeCapabilitySnapshot(true, "Available."));
            SyncApplicationService service = CreateService(
                store,
                syncStateStore: syncStateStore,
                validator: validator);
            SyncPairSettings changed = CopySyncPair(syncPair);
            changed.Mode = SyncPairMode.WindowsVirtualFiles;
            changed.UpdatedAtUtc = DateTime.UtcNow;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(changed);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(result.Validation.Errors.Select(error => error.Issue), Is.EqualTo(new[]
                {
                    SyncPairValidationIssue.SyncScopeChangeNotSupported,
                }));
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.Mode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(syncStateStore.InitializeCallCount, Is.Zero);
                Assert.That(syncStateStore.DeletedSyncPairIds, Is.Empty);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_ValidatesPrerequisitesWhenDisabledPairIsEnabled()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            syncPair.IsEnabled = false;
            await store.UpsertAsync(syncPair);
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([
                new SyncPairValidationError(
                    SyncPairValidationIssue.LocalRootUnavailable,
                    syncPair.Id,
                    null,
                    "Local root unavailable."),
            ]);
            SyncApplicationService service = CreateService(store, prerequisites);
            SyncPairSettings enabled = CopySyncPair(syncPair);
            enabled.IsEnabled = true;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(enabled);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(prerequisites.CallCount, Is.EqualTo(1));
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.IsEnabled, Is.False);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_EnablesDisabledPairAndRestartsSyncComponentsWhenCoreIsRunning()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            syncPair.IsEnabled = false;
            await store.UpsertAsync(syncPair);
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([]);
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                store,
                prerequisites,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);
            await service.StartSyncAsync();
            SyncPairSettings enabled = CopySyncPair(syncPair);
            enabled.IsEnabled = true;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(enabled);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(prerequisites.CallCount, Is.EqualTo(1));
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.IsEnabled, Is.True);
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
        public async Task SaveSyncPairAsync_EnablesDisabledPairAndReappliesGlobalPauseWhenCoreIsRunning()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            syncPair.IsEnabled = false;
            await store.UpsertAsync(syncPair);
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([]);
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                store,
                prerequisites,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);
            await service.StartSyncAsync();
            await service.PauseAllAsync();
            SyncPairSettings enabled = CopySyncPair(syncPair);
            enabled.IsEnabled = true;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(enabled);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(prerequisites.CallCount, Is.EqualTo(1));
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.IsEnabled, Is.True);
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StartCallCount, Is.EqualTo(2));
                Assert.That(supervisor.PauseAllCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastStartPaused, Is.True);
                Assert.That(localChanges.StartCallCount, Is.EqualTo(2));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(2));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_SkipsPrerequisitesWhenStructuralValidationFails()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([]);
            SyncApplicationService service = CreateService(store, prerequisites);
            SyncPairSettings existing = CreatePair("/home/user/Cotton");
            SyncPairSettings overlapping = CreatePair("/home/user/Cotton/Work");
            await service.SaveSyncPairAsync(existing);

            await service.SaveSyncPairAsync(overlapping);

            Assert.That(prerequisites.CallCount, Is.EqualTo(1));
        }

    }
}
