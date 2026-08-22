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
        public async Task AddSyncPairAsync_ReportsInitialSyncFailureWithoutRollingBackSavedPair()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.SyncNowException = new InvalidOperationException("Sync changes API is unavailable.");
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory);
            string localPath = Path.Combine(_tempDirectory, "Downloads");
            Directory.CreateDirectory(localPath);
            TaskCompletionSource<DesktopActivitySnapshot> activityReported = new TaskCompletionSource<DesktopActivitySnapshot>(
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
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
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
        public async Task RemoveSyncPairAsync_UsesActiveHostAppWithoutManualRestart()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            Uri serverUrl = new("https://cotton.example.test/");
            SqliteSyncPairSettingsStore syncPairStore = new SqliteSyncPairSettingsStore(paths.AppDatabasePath);
            await syncPairStore.InitializeAsync();
            SyncPairSettings syncPair = CreateSyncPair(isEnabled: true);
            await syncPairStore.UpsertAsync(syncPair);
            FakeDesktopApplicationHost host = FakeDesktopApplicationHost.Create(serverUrl);
            host.App.StartSyncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueingDesktopSyncApplicationFactory factory = new QueueingDesktopSyncApplicationFactory(host.Host);
            using DesktopShellController controller = CreateController(paths, factory, syncPairStore: syncPairStore);
            await controller.SignInAsync(new DesktopSignInRequest(
                serverUrl.AbsoluteUri,
                "desktop@example.test",
                "password",
                null));
            await host.App.StartSyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await controller.RemoveSyncPairAsync(syncPair.Id);

            Assert.Multiple(() =>
            {
                Assert.That(host.App.DeleteSyncPairCalls, Is.EqualTo(1));
                Assert.That(host.App.DeletedSyncPairId, Is.EqualTo(syncPair.Id));
                Assert.That(host.App.StopSyncCalls, Is.Zero);
                Assert.That(host.App.StartSyncCalls, Is.EqualTo(1));
            });
        }
    }
}
