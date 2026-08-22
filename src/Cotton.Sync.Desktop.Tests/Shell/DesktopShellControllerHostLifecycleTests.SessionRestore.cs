// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopShellControllerHostLifecycleTests
    {
        [Test]
        public async Task LoadAsync_SkipsSessionRestoreWhenTokenStorageIsInsecure()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory();
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                tokenStorageCapabilities: CreateInsecureTokenStorage);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ServerUrl, Is.EqualTo(serverUrl));
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(factory.CreatedServerUrls, Is.Empty);
            });
        }

        [Test]
        public async Task LoadAsync_ClearsStoredSessionWhenRestoreIsUnauthorized()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionException = new CottonApiException(
                HttpStatusCode.Unauthorized,
                null,
                "Unauthorized");
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.HasStoredSession, Is.False);
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.EqualTo(1));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task LoadAsync_PreservesStoredSessionWhenServerIsLocked()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionException = new CottonApiException(
                HttpStatusCode.Locked,
                "{\"locked\":true,\"message\":\"Cotton is locked until the master key is provided.\"}",
                "Cotton API request GET /api/v1/auth/me failed with status 423 (Locked).");
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                savedSessionRestoreRetryBaseDelay: TimeSpan.Zero);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.HasStoredSession, Is.True);
                Assert.That(
                    snapshot.StartupErrorMessage,
                    Is.EqualTo("Cotton Cloud reports that the server is locked. Unlock it in the web app; Cotton Sync will retry automatically."));
                Assert.That(host.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.Zero);
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RestoreStoredSessionAsync_ReconnectsAfterTemporaryServerLock()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeCottonTokenStore tokenStore = new FakeCottonTokenStore();
            FakeDesktopApplicationHost lockedHost = FakeDesktopApplicationHost.Create(serverUrl, tokenStore);
            lockedHost.App.RestoreSessionException = new CottonApiException(
                HttpStatusCode.Locked,
                "{\"locked\":true}",
                "Cotton API request GET /api/v1/auth/me failed with status 423 (Locked).");
            FakeDesktopApplicationHost restoredHost = FakeDesktopApplicationHost.Create(serverUrl, tokenStore);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(lockedHost.Host, restoredHost.Host);
            await using DesktopShellController controller = CreateController(
                paths,
                factory,
                savedSessionRestoreRetryBaseDelay: TimeSpan.Zero);

            DesktopShellSnapshot initial = await controller.LoadAsync();
            DesktopStoredSessionRestoreSnapshot restored = await controller
                .RestoreStoredSessionAsync(serverUrl.AbsoluteUri);

            Assert.Multiple(() =>
            {
                Assert.That(initial.HasStoredSession, Is.True);
                Assert.That(initial.IsSignedIn, Is.False);
                Assert.That(restored.HasStoredSession, Is.True);
                Assert.That(restored.Session, Is.Not.Null);
                Assert.That(restored.Session!.Username, Is.EqualTo("restored"));
                Assert.That(tokenStore.ClearAsyncCalls, Is.Zero);
                Assert.That(factory.CreatedServerUrls, Is.EqualTo(new[] { serverUrl, serverUrl }));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsRejectedSessionRestoreSeparatelyFromRefreshNoise()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionException = new CottonApiException(
                HttpStatusCode.Unauthorized,
                null,
                "Cotton API request GET /api/v1/me failed with status 401 (Unauthorized).");
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            await controller.LoadAsync();
            JsonElement auth = await ReadDiagnosticsRootAsync(controller, "auth");

            Assert.Multiple(() =>
            {
                Assert.That(auth.GetProperty("lastSessionRestoreStatus").GetString(), Is.EqualTo("rejected"));
                Assert.That(auth.GetProperty("lastSessionRestoreFailureType").GetString(), Is.EqualTo(nameof(CottonApiException)));
                Assert.That(auth.GetProperty("lastSessionRestoreAttempts").GetInt32(), Is.EqualTo(1));
                Assert.That(auth.GetProperty("lastTokenRefreshStatus").GetString(), Is.EqualTo("notObserved"));
                Assert.That(auth.GetProperty("lastSessionRestoreFailureMessage").GetString(), Does.Contain("Unauthorized"));
            });
        }

        [Test]
        public async Task LoadAsync_ReportsTransientSessionRestoreFailureInsteadOfSigningOut()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionException = new CottonApiException(
                HttpStatusCode.InternalServerError,
                null,
                "Internal Server Error");
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ServerUrl, Is.EqualTo(serverUrl));
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.StartupErrorMessage, Is.Not.Empty);
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.Zero);
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task LoadAsync_RetriesTransientSessionRestoreFailureAndKeepsStoredSession()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionExceptions.Enqueue(new HttpRequestException(
                "Firewall blocked first restore request.",
                new System.Net.Sockets.SocketException(10013)));
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                savedSessionRestoreRetryBaseDelay: TimeSpan.Zero);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.IsSignedIn, Is.True);
                Assert.That(snapshot.StartupErrorMessage, Is.Null);
                Assert.That(host.App.RestoreSessionCalls, Is.EqualTo(2));
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.Zero);
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.Zero);
            });
        }

        [Test]
        public async Task LoadAsync_BoundsTokenStorageVerificationBeforeSessionRestore()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            TaskCompletionSource<bool> verificationCancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(
                FakeDesktopApplicationHost.Create(serverUrl).Host);
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                tokenStorageVerifier: async cancellationToken =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () => verificationCancelled.TrySetResult(true));
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return CreateSecureTokenStorage();
                },
                tokenStorageVerificationTimeout: TimeSpan.FromMilliseconds(50));

            DesktopShellSnapshot snapshot = await controller
                .LoadAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(verificationCancelled.Task.IsCompletedSuccessfully, Is.True);
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.ServerUrl, Is.EqualTo(serverUrl));
                Assert.That(factory.CreatedServerUrls, Is.Empty);
            });
        }

        [Test]
        public async Task LoadAsync_AppliesDefaultAutostartBeforeSessionExists()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory();
            FakeAutostartService autostartService = new FakeAutostartService();
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                autostartService: autostartService);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.StartWithOperatingSystem, Is.True);
                Assert.That(autostartService.IsEnabledCalls, Is.EqualTo(2));
                Assert.That(autostartService.SetEnabledCalls, Is.EqualTo(1));
                Assert.That(autostartService.LastSetEnabled, Is.True);
            });
        }

        [Test]
        public async Task LoadAsync_DoesNotReenableAutostartWhenPreferenceIsDisabled()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                StartWithOperatingSystem = false,
            });

            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory();
            FakeAutostartService autostartService = new FakeAutostartService();
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                autostartService: autostartService);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.StartWithOperatingSystem, Is.False);
                Assert.That(autostartService.IsEnabledCalls, Is.EqualTo(1));
                Assert.That(autostartService.SetEnabledCalls, Is.Zero);
            });
        }

        [Test]
        public async Task SignInAsync_AppliesDefaultAutostartAfterAuthentication()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            FakeAutostartService autostartService = new FakeAutostartService();
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                autostartService: autostartService);

            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));

            Assert.Multiple(() =>
            {
                Assert.That(autostartService.SetEnabledCalls, Is.EqualTo(1));
                Assert.That(autostartService.LastSetEnabled, Is.True);
            });
        }

    }
}
