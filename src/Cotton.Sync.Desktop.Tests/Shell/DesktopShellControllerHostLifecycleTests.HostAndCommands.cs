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
        public async Task LoadAsync_ReusesActiveRestoredHostWithoutRestartingSync()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost firstHost = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new(firstHost.Host);
            using DesktopShellController controller = CreateController(paths, factory);

            DesktopShellSnapshot firstSnapshot = await controller.LoadAsync();
            DesktopShellSnapshot secondSnapshot = await controller.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(firstSnapshot.IsSignedIn, Is.True);
                Assert.That(secondSnapshot.IsSignedIn, Is.True);
                Assert.That(factory.CreatedServerUrls, Is.EqualTo(new[] { serverUrl }));
                Assert.That(firstHost.App.RestoreSessionCalls, Is.EqualTo(1));
                Assert.That(firstHost.App.StartSyncCalls, Is.EqualTo(1));
                Assert.That(firstHost.App.StopSyncCalls, Is.Zero);
                Assert.That(firstHost.AsyncResource.DisposeAsyncCalls, Is.Zero);
            });
        }

        [Test]
        public async Task DisposeAsync_StopsActiveRestoredHost()
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
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            FakeCottonTokenStore tokenStore = new FakeCottonTokenStore(hasStoredTokens: false);
            FakeDesktopApplicationHost signedInHost = FakeDesktopApplicationHost.Create(serverUrl, tokenStore);
            FakeDesktopApplicationHost restoredHost = FakeDesktopApplicationHost.Create(serverUrl, tokenStore);
            signedInHost.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            restoredHost.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signedInHost.App.PreferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(signedInHost.Host, restoredHost.Host);

            await using (DesktopShellController signedInController = CreateController(paths, factory))
            {
                AuthSession session = await signedInController.SignInAsync(new DesktopSignInRequest(
                    serverUrl.AbsoluteUri,
                    " desktop@example.test ",
                    "password",
                    null));
                await signedInHost.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

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
            await restoredHost.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

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
                Assert.That(restoredHost.App.StartSyncCalls, Is.EqualTo(1));
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
            SqliteAppPreferencesStore preferencesStore = new SqliteAppPreferencesStore(paths.AppDatabasePath);
            await preferencesStore.InitializeAsync();
            await preferencesStore.SaveAsync(new AppPreferences
            {
                RememberedServerUrl = serverUrl,
            });
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory();
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
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
                    Assert.That(host.App.LastSyncNowRequest?.Causes, Is.EqualTo(SyncRunCause.InitialPopulation));
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
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
        public async Task SyncAllAsync_WithRemoteDeleteApprovalTargetsExactSyncPair()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            Guid syncPairId = Guid.NewGuid();
            RemoteDeletePlanApproval approval = new(101, new string('a', 64));

            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);
            await controller.SyncAllAsync(syncPairId: syncPairId, approvedRemoteDeletePlan: approval);

            Assert.Multiple(() =>
            {
                Assert.That(host.App.SyncNowCalls, Is.EqualTo(1));
                Assert.That(host.App.LastSyncNowPairId, Is.EqualTo(syncPairId));
                Assert.That(host.App.LastSyncNowRequest?.IsFull, Is.True);
                Assert.That(host.App.LastSyncNowRequest?.Causes, Is.EqualTo(SyncRunCause.Manual));
                Assert.That(host.App.LastSyncNowRequest?.ApprovedRemoteDeletePlan, Is.EqualTo(approval));
            });
        }

        [Test]
        public async Task SyncAllAsync_RejectsRemoteDeleteApprovalWithoutSyncPair()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            RemoteDeletePlanApproval approval = new(101, new string('a', 64));

            await controller.SignInWithBrowserAsync(serverUrl.AbsoluteUri);

            Assert.ThrowsAsync<ArgumentException>(
                async () => await controller.SyncAllAsync(approvedRemoteDeletePlan: approval));
        }

    }
}
