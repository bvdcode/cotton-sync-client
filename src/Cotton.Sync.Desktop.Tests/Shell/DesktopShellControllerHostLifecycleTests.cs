// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
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
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public class DesktopShellControllerHostLifecycleTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            DesktopAuthDiagnosticsState.ResetForTests();
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-shell-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public async Task LoadAsync_StopsPreviousRestoredHostWhenSessionIsRestoredAgain()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost firstHost = FakeDesktopApplicationHost.Create(serverUrl);
            FakeDesktopApplicationHost secondHost = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(firstHost.Host, secondHost.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot firstSnapshot = await controller.LoadAsync();
            DesktopShellSnapshot secondSnapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(firstSnapshot.IsSignedIn, Is.True);
                Assert.That(secondSnapshot.IsSignedIn, Is.True);
                Assert.That(factory.CreatedServerUrls, Is.EqualTo(new[] { serverUrl, serverUrl }));
                Assert.That(firstHost.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(firstHost.App.StopSyncCalls, Is.EqualTo(1));
                Assert.That(firstHost.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(secondHost.App.StopSyncCalls, Is.Zero);
                Assert.That(secondHost.AsyncResource.DisposeAsyncCalls, Is.Zero);
            });
        }

        [Test]
        public async Task DisposeAsync_StopsActiveRestoredHost()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            DesktopShellController controller = CreateController(paths, factory);

            await controller.LoadAsync();
            await controller.DisposeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(host.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(host.App.StopSyncCalls, Is.EqualTo(1));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task LoadAsync_RestoresSignedInSessionAfterControllerRelaunch()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var tokenStore = new FakeCottonTokenStore(hasStoredTokens: false);
            FakeDesktopApplicationHost signedInHost = FakeDesktopApplicationHost.Create(serverUrl, tokenStore);
            FakeDesktopApplicationHost restoredHost = FakeDesktopApplicationHost.Create(serverUrl, tokenStore);
            signedInHost.App.PreferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            var factory = new QueueingDesktopSyncApplicationFactory(signedInHost.Host, restoredHost.Host);

            await using (DesktopShellController signedInController = CreateController(paths, factory))
            {
                AuthSession session = await signedInController.SignInAsync(new DesktopSignInRequest(
                    serverUrl.AbsoluteUri,
                    " desktop@example.test ",
                    "password",
                    null));

                Assert.Multiple(() =>
                {
                    Assert.That(session.Email, Is.EqualTo("desktop@example.test"));
                    Assert.That(tokenStore.SaveAsyncCalls, Is.EqualTo(1));
                    Assert.That(signedInHost.App.StartSyncCalls, Is.EqualTo(1));
                });

                await signedInController.DisposeAsync();
            }

            await using DesktopShellController restoredController = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await restoredController.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ServerUrl, Is.EqualTo(serverUrl));
                Assert.That(snapshot.IsSignedIn, Is.True);
                Assert.That(snapshot.AccountName, Is.EqualTo("restored@example.test"));
                Assert.That(snapshot.RememberedUsername, Is.EqualTo("desktop@example.test"));
                Assert.That(snapshot.StartupErrorMessage, Is.Null);
                Assert.That(factory.CreatedServerUrls, Is.EqualTo(new[] { serverUrl, serverUrl }));
                Assert.That(signedInHost.App.StopSyncCalls, Is.EqualTo(1));
                Assert.That(signedInHost.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
                Assert.That(restoredHost.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(tokenStore.ClearAsyncCalls, Is.Zero);
            });
        }

        [Test]
        public void HostDispose_DisposesAsyncResource()
        {
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(new Uri("https://cotton.example.test/"));

            host.Host.Dispose();
            host.Host.Dispose();

            Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
        }

        [Test]
        public void HostDispose_DoesNotRetryAsyncResourceWhenDisposeFails()
        {
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(new Uri("https://cotton.example.test/"));
            host.AsyncResource.DisposeException = new InvalidOperationException("dispose failed");

            InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(host.Host.Dispose);
            host.Host.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Is.EqualTo("dispose failed"));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task HostDisposeAsync_DoesNotRetryAsyncResourceWhenDisposeFails()
        {
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(new Uri("https://cotton.example.test/"));
            host.AsyncResource.DisposeException = new InvalidOperationException("dispose failed");

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await host.Host.DisposeAsync());
            await host.Host.DisposeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Is.EqualTo("dispose failed"));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Dispose_StopsActiveRestoredHost()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            DesktopShellController controller = CreateController(paths, factory);

            await controller.LoadAsync();
            controller.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(host.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(host.App.StopSyncCalls, Is.EqualTo(1));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SignInAsync_RejectsInsecureTokenStorageBeforeCreatingHost()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new QueueingDesktopSyncApplicationFactory();
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                tokenStorageCapabilities: CreateInsecureTokenStorage);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await controller.SignInAsync(
                    new DesktopSignInRequest(
                        "https://cotton.example.test/",
                        "desktop@example.test",
                        "password",
                        string.Empty)));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("Secure token storage is unavailable"));
                Assert.That(factory.CreatedServerUrls, Is.Empty);
            });
        }

        [Test]
        public async Task SignInWithBrowserAsync_ReturnsBeforeSyncCoreStartCompletes()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.App.StartSyncRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            try
            {
                AuthSession session = await controller
                    .SignInWithBrowserAsync(serverUrl.AbsoluteUri)
                    .WaitAsync(TimeSpan.FromSeconds(2));

                await host.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(session.Email, Does.EndWith("@example.test"));
                    Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
                    Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.Zero);
                });
            }
            finally
            {
                host.App.StartSyncRelease.TrySetResult();
            }
        }

        [Test]
        public async Task AddSyncPairAsync_ReturnsBeforeInitialSyncCompletes()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.SyncNowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.App.SyncNowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            try
            {
                await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);

                Task<SyncPairSettings> addTask = controller.AddSyncPairAsync(
                    new DesktopSyncPairRequest(
                        Path.Combine(_tempDirectory, "Cloud"),
                        "/Cloud",
                        SyncPairMode.WindowsVirtualFiles));
                SyncPairSettings syncPair = await addTask.WaitAsync(TimeSpan.FromSeconds(2));
                await host.App.SyncNowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(syncPair.RemoteDisplayPath, Is.EqualTo("/Cloud"));
                    Assert.That(syncPair.Mode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
                    Assert.That(host.App.SaveSyncPairCalls, Is.EqualTo(1));
                    Assert.That(host.App.SyncNowCalls, Is.EqualTo(1));
                    Assert.That(host.App.DeleteSyncPairCalls, Is.Zero);
                });
            }
            finally
            {
                host.App.SyncNowRelease.TrySetResult();
            }
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsZeroPairBackgroundLifecycle()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.App.StartSyncRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            try
            {
                await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
                await host.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                JsonElement startingLifecycle = await ReadSyncLifecycleDiagnosticsAsync(controller);
                Assert.Multiple(() =>
                {
                    Assert.That(startingLifecycle.GetProperty("isSignedIn").GetBoolean(), Is.True);
                    Assert.That(startingLifecycle.GetProperty("syncCoreState").GetString(), Is.EqualTo("starting"));
                    Assert.That(startingLifecycle.GetProperty("isBackgroundActive").GetBoolean(), Is.True);
                    Assert.That(startingLifecycle.GetProperty("syncPairCount").GetInt32(), Is.Zero);
                    Assert.That(startingLifecycle.GetProperty("enabledSyncPairCount").GetInt32(), Is.Zero);
                    Assert.That(startingLifecycle.GetProperty("hasNoSyncPairs").GetBoolean(), Is.True);
                    Assert.That(startingLifecycle.GetProperty("isZeroPairBackgroundActive").GetBoolean(), Is.True);
                    Assert.That(
                        startingLifecycle.GetProperty("status").GetString(),
                        Is.EqualTo("zeroPairBackgroundActive"));
                });
            }
            finally
            {
                host.App.StartSyncRelease.TrySetResult();
            }
        }

        [Test]
        public async Task RemoveSyncPairAsync_MarksZeroPairBackgroundInactiveAfterLastPairDeletion()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.SyncPairStore = syncPairStore;
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);

            JsonElement beforeRemove = await ReadSyncLifecycleDiagnosticsAsync(controller);
            await controller.RemoveSyncPairAsync(syncPair.Id);
            JsonElement afterRemove = await ReadSyncLifecycleDiagnosticsAsync(controller);

            Assert.Multiple(() =>
            {
                Assert.That(beforeRemove.GetProperty("status").GetString(), Is.EqualTo("configuredPairs"));
                Assert.That(beforeRemove.GetProperty("syncPairCount").GetInt32(), Is.EqualTo(1));
                Assert.That(beforeRemove.GetProperty("isBackgroundActive").GetBoolean(), Is.True);
                Assert.That(afterRemove.GetProperty("status").GetString(), Is.EqualTo("zeroPairBackgroundInactive"));
                Assert.That(afterRemove.GetProperty("syncPairCount").GetInt32(), Is.Zero);
                Assert.That(afterRemove.GetProperty("syncCoreState").GetString(), Is.EqualTo("stopped"));
                Assert.That(afterRemove.GetProperty("isBackgroundActive").GetBoolean(), Is.False);
                Assert.That(host.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.DeletedSyncPairId, Is.EqualTo(syncPair.Id));
            });
        }

        [Test]
        public async Task LoadAsync_SkipsSessionRestoreWhenTokenStorageIsInsecure()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            var factory = new QueueingDesktopSyncApplicationFactory();
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
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
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
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(host.TokenStore.ClearAsyncCalls, Is.EqualTo(1));
                Assert.That(host.AsyncResource.DisposeAsyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsRejectedSessionRestoreSeparatelyFromRefreshNoise()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
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
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
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
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.RestoreSessionExceptions.Enqueue(new HttpRequestException(
                "Firewall blocked first restore request.",
                new System.Net.Sockets.SocketException(10013)));
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            var factory = new QueueingDesktopSyncApplicationFactory(
                FakeDesktopApplicationHost.Create(serverUrl).Host);
            using DesktopShellController controller = CreateController(
                paths,
                factory,
                tokenStorageVerifier: async cancellationToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return CreateSecureTokenStorage();
                },
                tokenStorageVerificationTimeout: TimeSpan.FromMilliseconds(50));
            Stopwatch stopwatch = Stopwatch.StartNew();

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
                Assert.That(snapshot.IsSignedIn, Is.False);
                Assert.That(snapshot.ServerUrl, Is.EqualTo(serverUrl));
                Assert.That(factory.CreatedServerUrls, Is.Empty);
            });
        }

        [Test]
        public async Task LoadAsync_AppliesDefaultAutostartBeforeSessionExists()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            var factory = new QueueingDesktopSyncApplicationFactory();
            var autostartService = new FakeAutostartService();
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
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                StartWithOperatingSystem = false,
            });

            var factory = new QueueingDesktopSyncApplicationFactory();
            var autostartService = new FakeAutostartService();
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
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            var autostartService = new FakeAutostartService();
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

        [Test]
        public async Task AddSyncPairAsync_ReportsInitialSyncFailureWithoutRollingBackSavedPair()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.SyncNowException = new InvalidOperationException("Sync changes API is unavailable.");
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            string localPath = Path.Combine(_tempDirectory, "Downloads");
            Directory.CreateDirectory(localPath);
            var activityReported = new TaskCompletionSource<DesktopActivitySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            controller.ActivityReported += (_, activity) =>
            {
                if (activity.Kind == "Error")
                {
                    activityReported.TrySetResult(activity);
                }
            };

            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));
            SyncPairSettings syncPair =
                await controller.AddSyncPairAsync(new DesktopSyncPairRequest(localPath, "/Downloads"));
            DesktopActivitySnapshot activity =
                await activityReported.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(syncPair.RemoteDisplayPath, Is.EqualTo("/Downloads"));
                Assert.That(activity.Path, Is.EqualTo(localPath));
                Assert.That(activity.Details, Does.Contain("Sync changes API is unavailable."));
                Assert.That(host.App.SaveSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.SyncNowCalls, Is.EqualTo(1));
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.DeleteSyncPairCalls, Is.Zero);
            });
        }

        [Test]
        public async Task AddSyncPairAsync_SavesRequestedWindowsVirtualFilesMode()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            string localPath = Path.Combine(_tempDirectory, "Desktop");
            Directory.CreateDirectory(localPath);

            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));
            SyncPairSettings syncPair = await controller.AddSyncPairAsync(
                new DesktopSyncPairRequest(localPath, "/Desktop", SyncPairMode.WindowsVirtualFiles));

            Assert.Multiple(() =>
            {
                Assert.That(syncPair.Mode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
                Assert.That(host.App.SavedSyncPair, Is.Not.Null);
                Assert.That(host.App.SavedSyncPair!.Mode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
                Assert.That(host.App.SyncNowCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SetSyncPairEnabledAsync_UsesActiveHostAppWithoutManualRestart()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));

            await controller.SetSyncPairEnabledAsync(syncPair.Id, enabled: false);

            Assert.Multiple(() =>
            {
                Assert.That(host.App.SaveSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.SavedSyncPair, Is.Not.Null);
                Assert.That(host.App.SavedSyncPair!.Id, Is.EqualTo(syncPair.Id));
                Assert.That(host.App.SavedSyncPair.IsEnabled, Is.False);
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RenameSyncPairAsync_UsesActiveHostAppWithoutManualRestart()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));

            await controller.RenameSyncPairAsync(syncPair.Id, "  Work documents  ");

            Assert.Multiple(() =>
            {
                Assert.That(host.App.SaveSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.SavedSyncPair, Is.Not.Null);
                Assert.That(host.App.SavedSyncPair!.Id, Is.EqualTo(syncPair.Id));
                Assert.That(host.App.SavedSyncPair.DisplayName, Is.EqualTo("Work documents"));
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SetSyncPairLocalFolderAsync_UsesActiveHostAppWithoutManualRestart()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));

            await controller.SetSyncPairLocalFolderAsync(syncPair.Id, "  /home/user/New Cotton  ");

            Assert.Multiple(() =>
            {
                Assert.That(host.App.SaveSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.SavedSyncPair, Is.Not.Null);
                Assert.That(host.App.SavedSyncPair!.Id, Is.EqualTo(syncPair.Id));
                Assert.That(host.App.SavedSyncPair.LocalRootPath, Is.EqualTo("/home/user/New Cotton"));
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SetSyncPairRemoteFolderAsync_UsesActiveHostAppWithoutManualRestart()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));

            SyncPairSettings saved = await controller.SetSyncPairRemoteFolderAsync(syncPair.Id, "  Documents Archive  ");

            Assert.Multiple(() =>
            {
                Assert.That(host.App.SaveSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.SavedSyncPair, Is.Not.Null);
                Assert.That(host.App.SavedSyncPair!.Id, Is.EqualTo(syncPair.Id));
                Assert.That(host.App.SavedSyncPair.RemoteDisplayPath, Is.EqualTo("/Documents Archive"));
                Assert.That(host.App.SavedSyncPair.RemoteRootNodeId, Is.EqualTo(Guid.Parse("11111111-1111-1111-1111-111111111111")));
                Assert.That(saved.RemoteDisplayPath, Is.EqualTo("/Documents Archive"));
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RemoveSyncPairAsync_UsesActiveHostAppWithoutManualRestart()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));

            await controller.RemoveSyncPairAsync(syncPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(host.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.DeletedSyncPairId, Is.EqualTo(syncPair.Id));
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task StatusChanged_ForwardsLastSuccessfulSyncTimestamp()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            var statusEvents = new List<DesktopSyncStatusSnapshot>();
            controller.StatusChanged += (_, status) => statusEvents.Add(status);
            Guid syncPairId = Guid.NewGuid();
            DateTime completedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            await controller.LoadAsync();
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPairId,
                        "Documents",
                        SyncPairRunState.Idle,
                        null,
                        null,
                        DateTime.UtcNow,
                        completedAtUtc),
                ],
                DateTime.UtcNow));

            DesktopSyncPairStatusSnapshot pairStatus = statusEvents.Last().SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(pairStatus.Id, Is.EqualTo(syncPairId));
                Assert.That(pairStatus.LastSyncedAtUtc, Is.EqualTo(completedAtUtc));
            });
        }

        [Test]
        public async Task SessionRevoked_ForwardsSessionRevocationEvents()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            var sessionRevocations = new List<DesktopSessionRevocationSnapshot>();
            DateTime occurredAtUtc = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
            controller.SessionRevoked += (_, sessionRevocation) => sessionRevocations.Add(sessionRevocation);

            await controller.LoadAsync();
            host.SessionRevocationPublisher.Publish(new SessionRevocationEvent(occurredAtUtc));

            Assert.Multiple(() =>
            {
                Assert.That(sessionRevocations, Has.Count.EqualTo(1));
                Assert.That(sessionRevocations[0].OccurredAtUtc, Is.EqualTo(occurredAtUtc));
            });
        }

        [Test]
        public async Task ExportDiagnosticsAsync_ReportsLastSessionRevocation()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            DateTime occurredAtUtc = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

            await controller.LoadAsync();
            host.SessionRevocationPublisher.Publish(new SessionRevocationEvent(occurredAtUtc));
            JsonElement auth = await ReadDiagnosticsRootAsync(controller, "auth");

            Assert.That(
                auth.GetProperty("lastSessionRevokedAtUtc").GetDateTimeOffset(),
                Is.EqualTo(new DateTimeOffset(occurredAtUtc)));
        }

        [Test]
        public async Task LoadAsync_UsesRuntimeLastSuccessfulSyncWhenBaselineIsEmpty()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            var preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            Guid syncPairId = Guid.NewGuid();
            var syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            await syncPairStore.UpsertAsync(new SyncPairSettings
            {
                Id = syncPairId,
                DisplayName = "Empty folder",
                LocalRootPath = Path.Combine(_tempDirectory, "Empty folder"),
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Empty folder",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            DateTime completedAtUtc = new(2026, 6, 4, 9, 30, 0, DateTimeKind.Utc);
            host.StatusPublisher.Publish(new SyncAppStatus(
                isAuthenticated: true,
                [
                    new SyncPairStatus(
                        syncPairId,
                        "Empty folder",
                        SyncPairRunState.Idle,
                        null,
                        null,
                        DateTime.UtcNow,
                        completedAtUtc),
                ],
                DateTime.UtcNow));
            var factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot snapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SyncPairs.Single().LastSyncedAtUtc, Is.EqualTo(completedAtUtc));
                Assert.That(File.Exists(paths.SyncStateDatabasePath), Is.True);
            });
        }

        private static DesktopShellController CreateController(
            DesktopAppPaths paths,
            IDesktopSyncApplicationFactory factory,
            Func<DesktopTokenStorageCapabilitySnapshot>? tokenStorageCapabilities = null,
            Func<CancellationToken, Task<DesktopTokenStorageCapabilitySnapshot>>? tokenStorageVerifier = null,
            TimeSpan? tokenStorageVerificationTimeout = null,
            TimeSpan? savedSessionRestoreRetryBaseDelay = null,
            IAutostartService? autostartService = null,
            SqliteSyncPairSettingsStore? syncPairStore = null)
        {
            return new DesktopShellController(
                paths,
                factory,
                new SqliteAppPreferencesStore(paths.AppDatabasePath),
                syncPairStore ?? new SqliteSyncPairSettingsStore(paths.AppDatabasePath),
                new FakePlatformCommandService(),
                autostartService ?? new FakeAutostartService(),
                tokenStorageCapabilities: tokenStorageCapabilities ?? CreateSecureTokenStorage,
                tokenStorageVerifier: tokenStorageVerifier,
                savedSessionRestoreRetryBaseDelay: savedSessionRestoreRetryBaseDelay,
                tokenStorageVerificationTimeout: tokenStorageVerificationTimeout);
        }

        private static async Task<JsonElement> ReadSyncLifecycleDiagnosticsAsync(DesktopShellController controller)
        {
            return await ReadDiagnosticsRootAsync(controller, "syncLifecycle");
        }

        private static async Task<JsonElement> ReadDiagnosticsRootAsync(
            DesktopShellController controller,
            string propertyName)
        {
            string archivePath = await controller.ExportDiagnosticsAsync();
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string diagnosticsJson = ReadEntry(archive, "diagnostics.json");
            using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.GetProperty(propertyName).Clone();
        }

        private static string ReadEntry(ZipArchive archive, string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(
                "Diagnostics archive entry is missing: " + entryName);
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static SyncPairSettings CreateSyncPair(bool isEnabled)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static DesktopTokenStorageCapabilitySnapshot CreateSecureTokenStorage()
        {
            return new DesktopTokenStorageCapabilitySnapshot(
                "test-secure",
                IsReleaseSecure: true,
                "Test secure token storage");
        }

        private static DesktopTokenStorageCapabilitySnapshot CreateInsecureTokenStorage()
        {
            return new DesktopTokenStorageCapabilitySnapshot(
                "restricted-file-v1",
                IsReleaseSecure: false,
                "Development fallback");
        }

        private class QueueingDesktopSyncApplicationFactory : IDesktopSyncApplicationFactory
        {
            private readonly Queue<DesktopSyncApplicationHost> _hosts;

            public QueueingDesktopSyncApplicationFactory(params DesktopSyncApplicationHost[] hosts)
            {
                _hosts = new Queue<DesktopSyncApplicationHost>(hosts);
            }

            public List<Uri> CreatedServerUrls { get; } = [];

            public DesktopSyncApplicationHost Create(Uri serverUrl)
            {
                CreatedServerUrls.Add(serverUrl);
                return _hosts.Dequeue();
            }
        }

        private class FakeDesktopApplicationHost
        {
            private FakeDesktopApplicationHost(Uri serverUrl, FakeCottonTokenStore? tokenStore)
            {
                TokenStore = tokenStore ?? new FakeCottonTokenStore();
                App = new FakeSyncApplicationService(TokenStore);
                AsyncResource = new FakeAsyncResource();
                StatusPublisher = new InMemoryAppStatusPublisher();
                SessionRevocationPublisher = new InMemorySessionRevocationPublisher();
                Host = new DesktopSyncApplicationHost(
                    App,
                    new FakeRemoteRootResolver(),
                    StatusPublisher,
                    new InMemoryAppActivityPublisher(),
                    SessionRevocationPublisher,
                    new InMemoryAppTransferProgressPublisher(),
                    new InMemoryAppRunProgressPublisher(),
                    TokenStore,
                    new FakeCottonNodeClient(),
                    new FakeCottonSyncClient(),
                    new HttpClient(),
                    serverUrl,
                    AsyncResource);
            }

            public FakeSyncApplicationService App { get; }

            public InMemoryAppStatusPublisher StatusPublisher { get; }

            public InMemorySessionRevocationPublisher SessionRevocationPublisher { get; }

            public FakeAsyncResource AsyncResource { get; }

            public FakeCottonTokenStore TokenStore { get; }

            public DesktopSyncApplicationHost Host { get; }

            public static FakeDesktopApplicationHost Create(Uri serverUrl, FakeCottonTokenStore? tokenStore = null)
            {
                return new FakeDesktopApplicationHost(serverUrl, tokenStore);
            }
        }

        private class FakeAsyncResource : IAsyncDisposable
        {
            public int DisposeAsyncCalls { get; private set; }

            public Exception? DisposeException { get; set; }

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCalls++;
                if (DisposeException is not null)
                {
                    throw DisposeException;
                }

                return ValueTask.CompletedTask;
            }
        }

        private class FakeSyncApplicationService : ISyncApplicationService
        {
            private readonly ICottonTokenStore _tokenStore;

            public FakeSyncApplicationService(ICottonTokenStore tokenStore)
            {
                _tokenStore = tokenStore;
            }

            public int RestoreSessionCalls { get; private set; }

            public int StopSyncCalls { get; private set; }

            public int StartSyncCalls { get; private set; }

            public int SaveSyncPairCalls { get; private set; }

            public int DeleteSyncPairCalls { get; private set; }

            public int SyncNowCalls { get; private set; }

            public SyncPairSettings? SavedSyncPair { get; private set; }

            public Guid? DeletedSyncPairId { get; private set; }

            public Exception? SyncNowException { get; set; }

            public Exception? RestoreSessionException { get; set; }

            public Queue<Exception> RestoreSessionExceptions { get; } = [];

            public IAppPreferencesStore? PreferencesStore { get; set; }

            public ISyncPairSettingsStore? SyncPairStore { get; set; }

            public TaskCompletionSource? StartSyncStarted { get; set; }

            public TaskCompletionSource? StartSyncRelease { get; set; }

            public TaskCompletionSource? SyncNowStarted { get; set; }

            public TaskCompletionSource? SyncNowRelease { get; set; }

            public async Task<AuthSession> SignInAsync(
                PasswordSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                await _tokenStore.SaveAsync(CreateTokenPair(request.Username), cancellationToken);
                return CreateSession(request.Username);
            }

            public async Task<AuthSession> SignInWithBrowserAsync(
                AppCodeBrowserSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                string username = request.DeviceName ?? "browser";
                await _tokenStore.SaveAsync(CreateTokenPair(username), cancellationToken);
                return CreateSession(username);
            }

            public Task<AuthSession> RestoreSessionAsync(CancellationToken cancellationToken = default)
            {
                RestoreSessionCalls++;
                if (RestoreSessionExceptions.TryDequeue(out Exception? queuedException))
                {
                    throw queuedException;
                }

                if (RestoreSessionException is not null)
                {
                    throw RestoreSessionException;
                }

                return Task.FromResult(CreateSession("restored"));
            }

            public Task SignOutAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<AppPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AppPreferences());
            }

            public async Task SavePreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
            {
                if (PreferencesStore is null)
                {
                    return;
                }

                await PreferencesStore.InitializeAsync(cancellationToken);
                await PreferencesStore.SaveAsync(preferences, cancellationToken);
            }

            public async Task<IReadOnlyList<SyncPairSettings>> ListSyncPairsAsync(CancellationToken cancellationToken = default)
            {
                if (SyncPairStore is null)
                {
                    return [];
                }

                await SyncPairStore.InitializeAsync(cancellationToken);
                return await SyncPairStore.ListAsync(cancellationToken);
            }

            public async Task<SyncPairSettings?> GetSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                if (SyncPairStore is null)
                {
                    return null;
                }

                await SyncPairStore.InitializeAsync(cancellationToken);
                return await SyncPairStore.GetAsync(syncPairId, cancellationToken);
            }

            public async Task<SyncPairSaveResult> SaveSyncPairAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                SaveSyncPairCalls++;
                SavedSyncPair = syncPair;
                if (SyncPairStore is not null)
                {
                    await SyncPairStore.InitializeAsync(cancellationToken);
                    await SyncPairStore.UpsertAsync(syncPair, cancellationToken);
                }

                return SyncPairSaveResult.Saved(new SyncPairValidationResult([]));
            }

            public async Task DeleteSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                DeleteSyncPairCalls++;
                DeletedSyncPairId = syncPairId;
                if (SyncPairStore is not null)
                {
                    await SyncPairStore.InitializeAsync(cancellationToken);
                    await SyncPairStore.DeleteAsync(syncPairId, cancellationToken);
                }
            }

            public Task StartSyncAsync(CancellationToken cancellationToken = default)
            {
                StartSyncCalls++;
                StartSyncStarted?.TrySetResult();
                return StartSyncRelease?.Task ?? Task.CompletedTask;
            }

            public Task SyncAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                SyncNowCalls++;
                SyncNowStarted?.TrySetResult();
                if (SyncNowException is not null)
                {
                    throw SyncNowException;
                }

                return SyncNowRelease?.Task ?? Task.CompletedTask;
            }

            public Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopSyncAsync(CancellationToken cancellationToken = default)
            {
                StopSyncCalls++;
                return Task.CompletedTask;
            }

            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            private static AuthSession CreateSession(string username)
            {
                string normalized = username.Trim();
                string email = normalized.Contains('@', StringComparison.Ordinal)
                    ? normalized
                    : normalized + "@example.test";
                return new AuthSession(Guid.NewGuid(), normalized, email, isTotpEnabled: false);
            }

            private static TokenPairDto CreateTokenPair(string username)
            {
                string normalized = username.Trim();
                return new TokenPairDto
                {
                    AccessToken = "access-token-" + normalized,
                    RefreshToken = "refresh-token-" + normalized,
                };
            }
        }

        private class FakeCottonTokenStore : ICottonTokenStore
        {
            private TokenPairDto? _tokens;

            public FakeCottonTokenStore(bool hasStoredTokens = true)
            {
                _tokens = hasStoredTokens
                    ? new TokenPairDto
                    {
                        AccessToken = "access-token",
                        RefreshToken = "refresh-token",
                    }
                    : null;
            }

            public int SaveAsyncCalls { get; private set; }

            public TokenPairDto? LastSavedTokens { get; private set; }

            public int ClearAsyncCalls { get; private set; }

            public Task<TokenPairDto?> GetAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_tokens is null ? null : Clone(_tokens));
            }

            public Task SaveAsync(TokenPairDto tokens, CancellationToken cancellationToken = default)
            {
                SaveAsyncCalls++;
                _tokens = Clone(tokens);
                LastSavedTokens = Clone(tokens);
                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                ClearAsyncCalls++;
                _tokens = null;
                return Task.CompletedTask;
            }

            private static TokenPairDto Clone(TokenPairDto tokens)
            {
                return new TokenPairDto
                {
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                };
            }
        }

        private class FakeRemoteRootResolver : IRemoteRootResolver
        {
            public Task<NodeDto> EnsureAsync(string? remotePath = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new NodeDto
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    LayoutId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    ParentId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Name = string.IsNullOrWhiteSpace(remotePath)
                        ? "Cloud"
                        : remotePath.Trim('/'),
                });
            }
        }

        private class FakeCottonNodeClient : ICottonNodeClient
        {
            public Task<NodeDto> ResolveAsync(string? path = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> GetAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeContentDto> GetChildrenAsync(
                Guid nodeId,
                int page = 1,
                int pageSize = 100,
                int depth = 0,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> CreateAsync(Guid parentId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> MoveAsync(Guid nodeId, Guid parentId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> RenameAsync(Guid nodeId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> UpdateMetadataAsync(
                Guid nodeId,
                IReadOnlyDictionary<string, string> metadata,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(Guid nodeId, bool skipTrash = false, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<RestoreOutcomeDto> RestoreAsync(
                Guid nodeId,
                RestoreItemRequestDto? request = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<List<NodeDto>> GetAncestorsAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private class FakeCottonSyncClient : ICottonSyncClient
        {
            public Task<SyncChangesResponseDto> GetChangesAsync(
                long sinceCursor = 0,
                int limit = 500,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangesResponseDto
                {
                    SinceCursor = sinceCursor,
                    NextCursor = sinceCursor,
                    HasMore = false,
                });
            }
        }

        private class FakeAutostartService : IAutostartService
        {
            public bool IsSupported => true;

            public int IsEnabledCalls { get; private set; }

            public int SetEnabledCalls { get; private set; }

            public bool? LastSetEnabled { get; private set; }

            public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
            {
                IsEnabledCalls++;
                return Task.FromResult(LastSetEnabled == true);
            }

            public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            {
                SetEnabledCalls++;
                LastSetEnabled = enabled;
                return Task.CompletedTask;
            }
        }

        private class FakePlatformCommandService : IPlatformCommandService
        {
            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
