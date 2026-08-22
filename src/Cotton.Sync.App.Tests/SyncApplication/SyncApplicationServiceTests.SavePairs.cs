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
        public async Task SaveSyncPairAsync_PersistsValidPair()
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

            SyncPairSaveResult result = await service.SaveSyncPairAsync(syncPair);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(result.Validation.IsValid, Is.True);
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.Id, Is.EqualTo(syncPair.Id));
                Assert.That(supervisor.StartCallCount, Is.Zero);
                Assert.That(localChanges.StartCallCount, Is.Zero);
                Assert.That(remoteChanges.StartCallCount, Is.Zero);
                Assert.That(periodicSync.StartCallCount, Is.Zero);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RestartsSyncComponentsWhenSyncCoreIsRunning()
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
            await service.StartSyncAsync();

            SyncPairSaveResult result = await service.SaveSyncPairAsync(CreatePair("/home/user/Cotton"));

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
        public async Task SaveSyncPairAsync_ReappliesGlobalPauseWhenSyncCoreRestarts()
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
            await service.StartSyncAsync();
            await service.PauseAllAsync();

            SyncPairSaveResult result = await service.SaveSyncPairAsync(CreatePair("/home/user/Cotton"));

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
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
        public async Task StartSyncAsync_RestoresPersistedGlobalPauseAfterAppRestart()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeAppPreferencesStore preferences = new FakeAppPreferencesStore();
            FakeSyncSupervisor firstSupervisor = new FakeSyncSupervisor();
            SyncApplicationService first = CreateService(
                store,
                preferences: preferences,
                supervisor: firstSupervisor);
            await first.StartSyncAsync();
            await first.PauseAllAsync();
            FakeSyncSupervisor secondSupervisor = new FakeSyncSupervisor();
            SyncApplicationService second = CreateService(
                store,
                preferences: preferences,
                supervisor: secondSupervisor);

            await second.StartSyncAsync();

            Assert.Multiple(() =>
            {
                Assert.That(preferences.Preferences.IsSyncPaused, Is.True);
                Assert.That(preferences.SaveCallCount, Is.EqualTo(1));
                Assert.That(firstSupervisor.PauseAllCallCount, Is.EqualTo(1));
                Assert.That(secondSupervisor.StartCallCount, Is.EqualTo(1));
                Assert.That(secondSupervisor.LastStartPaused, Is.True);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_DoesNotRestartSyncComponentsWhenValidationFails()
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
            await service.StartSyncAsync();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(supervisor.StopCallCount, Is.Zero);
                Assert.That(localChanges.StopCallCount, Is.Zero);
                Assert.That(remoteChanges.StopCallCount, Is.Zero);
                Assert.That(periodicSync.StopCallCount, Is.Zero);
                Assert.That(supervisor.StartCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RejectsOverlappingPairWithoutPersisting()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncApplicationService service = CreateService(store);
            SyncPairSettings existing = CreatePair("/home/user/Cotton");
            SyncPairSettings overlapping = CreatePair("/home/user/Cotton/Work");
            await service.SaveSyncPairAsync(existing);

            SyncPairSaveResult result = await service.SaveSyncPairAsync(overlapping);

            IReadOnlyList<SyncPairSettings> savedPairs = await store.ListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(result.Validation.IsValid, Is.False);
                Assert.That(
                    result.Validation.Errors.Select(error => error.Issue),
                    Does.Contain(SyncPairValidationIssue.OverlappingLocalRoots));
                Assert.That(savedPairs.Select(pair => pair.Id), Is.EqualTo(new[] { existing.Id }));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_UpdatesExistingPairWithoutSelfOverlap()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncApplicationService service = CreateService(store);
            SyncPairSettings existing = CreatePair("/home/user/Cotton");
            await service.SaveSyncPairAsync(existing);
            SyncPairSettings updated = CopySyncPair(existing);
            updated.DisplayName = "Cotton Documents";
            updated.LocalRootPath = "/home/user/Cotton/";

            SyncPairSaveResult result = await service.SaveSyncPairAsync(updated);

            SyncPairSettings? saved = await store.GetAsync(existing.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(result.Validation.IsValid, Is.True);
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.DisplayName, Is.EqualTo("Cotton Documents"));
                Assert.That(saved.LocalRootPath, Is.EqualTo("/home/user/Cotton/"));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_RejectsPrerequisiteFailureWithoutPersisting()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([
                new SyncPairValidationError(
                    SyncPairValidationIssue.LocalRootUnavailable,
                    syncPair.Id,
                    null,
                    "Local root unavailable."),
            ]);
            SyncApplicationService service = CreateService(store, prerequisites);

            SyncPairSaveResult result = await service.SaveSyncPairAsync(syncPair);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.False);
                Assert.That(result.Validation.Errors.Select(error => error.Issue), Is.EqualTo(new[]
                {
                    SyncPairValidationIssue.LocalRootUnavailable,
                }));
                Assert.That(saved, Is.Null);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_SkipsPrerequisitesForDisabledPair()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            syncPair.IsEnabled = false;
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([
                new SyncPairValidationError(
                    SyncPairValidationIssue.LocalRootUnavailable,
                    syncPair.Id,
                    null,
                    "Local root unavailable."),
            ]);
            SyncApplicationService service = CreateService(store, prerequisites);

            SyncPairSaveResult result = await service.SaveSyncPairAsync(syncPair);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(prerequisites.CallCount, Is.Zero);
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.IsEnabled, Is.False);
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_SkipsPrerequisitesWhenOnlyDisplayNameChanges()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await store.UpsertAsync(syncPair);
            FakeSyncPairPrerequisiteValidator prerequisites = new FakeSyncPairPrerequisiteValidator([
                new SyncPairValidationError(
                    SyncPairValidationIssue.LocalRootUnavailable,
                    syncPair.Id,
                    null,
                    "Local root unavailable."),
            ]);
            SyncApplicationService service = CreateService(store, prerequisites);
            SyncPairSettings renamed = CopySyncPair(syncPair);
            renamed.DisplayName = "Renamed documents";
            renamed.UpdatedAtUtc = DateTime.UtcNow;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(renamed);

            SyncPairSettings? saved = await store.GetAsync(syncPair.Id);
            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(prerequisites.CallCount, Is.Zero);
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved!.DisplayName, Is.EqualTo("Renamed documents"));
            });
        }

        [Test]
        public async Task SaveSyncPairAsync_DoesNotDeletePersistedSyncStateWhenOnlyDisplayNameChanges()
        {
            InMemorySyncPairSettingsStore store = new InMemorySyncPairSettingsStore();
            FakeSyncStateStore syncStateStore = new FakeSyncStateStore();
            SyncPairSettings syncPair = CreatePair("/home/user/Cotton");
            await store.UpsertAsync(syncPair);
            SyncApplicationService service = CreateService(store, syncStateStore: syncStateStore);
            SyncPairSettings renamed = CopySyncPair(syncPair);
            renamed.DisplayName = "Renamed documents";
            renamed.UpdatedAtUtc = DateTime.UtcNow;

            SyncPairSaveResult result = await service.SaveSyncPairAsync(renamed);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSaved, Is.True);
                Assert.That(syncStateStore.InitializeCallCount, Is.Zero);
                Assert.That(syncStateStore.DeletedSyncPairIds, Is.Empty);
            });
        }
    }
}
