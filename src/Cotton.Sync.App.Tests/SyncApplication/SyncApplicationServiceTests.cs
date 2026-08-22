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
        public async Task SignInAsync_DelegatesToAuthFlow()
        {
            FakeAuthFlow authFlow = new FakeAuthFlow();
            SyncApplicationService service = CreateService(new InMemorySyncPairSettingsStore(), authFlow: authFlow);
            PasswordSignInRequest request = new PasswordSignInRequest
            {
                Username = "vadim",
                Password = "password",
            };

            AuthSession session = await service.SignInAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(authFlow.SignInCallCount, Is.EqualTo(1));
                Assert.That(authFlow.LastSignInRequest, Is.SameAs(request));
                Assert.That(session, Is.SameAs(authFlow.Session));
            });
        }

        [Test]
        public async Task SignInWithBrowserAsync_DelegatesToAppCodeBrowserAuthFlow()
        {
            FakeAppCodeBrowserAuthFlow appCodeBrowserAuthFlow = new FakeAppCodeBrowserAuthFlow();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                appCodeBrowserAuthFlow: appCodeBrowserAuthFlow);
            AppCodeBrowserSignInRequest request = new AppCodeBrowserSignInRequest
            {
                ApplicationName = "Cotton Sync Desktop",
                ApplicationVersion = "1.2.3",
                DeviceName = "workstation",
            };

            AuthSession session = await service.SignInWithBrowserAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(appCodeBrowserAuthFlow.SignInCallCount, Is.EqualTo(1));
                Assert.That(appCodeBrowserAuthFlow.LastSignInRequest, Is.SameAs(request));
                Assert.That(session, Is.SameAs(appCodeBrowserAuthFlow.Session));
            });
        }

        [Test]
        public async Task SignOutAsync_SignsOutAndStopsSupervisor()
        {
            FakeAuthFlow authFlow = new FakeAuthFlow();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                authFlow: authFlow,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);

            await service.SignOutAsync();

            Assert.Multiple(() =>
            {
                Assert.That(authFlow.SignOutCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StopCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RestoreSessionAsync_RestoresAuthOnly()
        {
            FakeAuthFlow authFlow = new FakeAuthFlow();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                authFlow: authFlow,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);

            AuthSession session = await service.RestoreSessionAsync();

            Assert.Multiple(() =>
            {
                Assert.That(authFlow.RestoreSessionCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StartCallCount, Is.Zero);
                Assert.That(localChanges.StartCallCount, Is.Zero);
                Assert.That(remoteChanges.StartCallCount, Is.Zero);
                Assert.That(periodicSync.StartCallCount, Is.Zero);
                Assert.That(session, Is.SameAs(authFlow.Session));
            });
        }

        [Test]
        public async Task GetPreferencesAsync_InitializesAndLoadsPreferences()
        {
            FakeAppPreferencesStore preferencesStore = new FakeAppPreferencesStore();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                preferences: preferencesStore);

            AppPreferences preferences = await service.GetPreferencesAsync();

            Assert.Multiple(() =>
            {
                Assert.That(preferencesStore.InitializeCallCount, Is.EqualTo(1));
                Assert.That(preferences, Is.SameAs(preferencesStore.Preferences));
            });
        }

        [Test]
        public async Task SavePreferencesAsync_InitializesAndSavesPreferences()
        {
            FakeAppPreferencesStore preferencesStore = new FakeAppPreferencesStore();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                preferences: preferencesStore);
            AppPreferences preferences = new AppPreferences
            {
                RememberedServerUrl = new Uri("https://cotton.example.test/"),
            };

            await service.SavePreferencesAsync(preferences);

            Assert.Multiple(() =>
            {
                Assert.That(preferencesStore.InitializeCallCount, Is.EqualTo(1));
                Assert.That(preferencesStore.SaveCallCount, Is.EqualTo(1));
                Assert.That(preferencesStore.SavedPreferences, Is.SameAs(preferences));
            });
        }

        [Test]
        public async Task SyncNowAsync_DelegatesToSupervisor()
        {
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor);
            Guid syncPairId = Guid.NewGuid();

            await service.SyncNowAsync(syncPairId);

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastSyncNowPairId, Is.EqualTo(syncPairId));
            });
        }

        [Test]
        public async Task OpenFolderAsync_DelegatesToPlatformCommands()
        {
            FakePlatformCommandService platformCommands = new FakePlatformCommandService();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                platformCommands: platformCommands);

            await service.OpenFolderAsync("/home/user/Cotton");

            Assert.Multiple(() =>
            {
                Assert.That(platformCommands.OpenFolderCallCount, Is.EqualTo(1));
                Assert.That(platformCommands.LastOpenedFolder, Is.EqualTo("/home/user/Cotton"));
            });
        }

        [Test]
        public async Task OpenWebAsync_DelegatesToPlatformCommands()
        {
            FakePlatformCommandService platformCommands = new FakePlatformCommandService();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                platformCommands: platformCommands);
            Uri url = new Uri("https://cotton.example.test/");

            await service.OpenWebAsync(url);

            Assert.Multiple(() =>
            {
                Assert.That(platformCommands.OpenWebCallCount, Is.EqualTo(1));
                Assert.That(platformCommands.LastOpenedUrl, Is.EqualTo(url));
            });
        }


    }
}
