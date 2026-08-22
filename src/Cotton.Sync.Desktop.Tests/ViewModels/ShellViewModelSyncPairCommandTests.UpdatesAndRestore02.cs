// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {

        [Test]
        public async Task InitializeAsync_AutoDownloadShowsProgressUntilInstallerIsReady()
        {
            TaskCompletionSource<DesktopUpdateStatusSnapshot> downloadCompletion = new TaskCompletionSource<DesktopUpdateStatusSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                UpdateDownloadSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    true,
                    "Update 0.0.2 is ready. Click Update to install it now, or it will install automatically on next app start.",
                    @"C:\Users\qa\AppData\Roaming\Cotton\Sync\updates\0.0.2\CottonSync-Windows-Setup.exe",
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                UpdateDownloadCompletion = downloadCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller, checkForUpdatesOnStartup: true);

            await viewModel.InitializeAsync();
            await WaitForAsync(() => controller.DownloadProgressReports.Count == 2);
            await WaitForAsync(() => viewModel.UpdateDetailsText.Contains("100%", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Downloading update"));
                Assert.That(viewModel.UpdateDetailsText, Does.Contain("1.0 KB / 1.0 KB"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Downloading update"));
                Assert.That(controller.DownloadUpdateSources, Is.EqualTo(new[] { DesktopUpdateCheckSource.Startup }));
            });

            downloadCompletion.SetResult(controller.UpdateDownloadSnapshot!);
            await viewModel.StartupUpdateTask!;

            Assert.That(viewModel.GlobalStatus, Is.EqualTo("Update ready"));
        }


        [Test]
        public async Task InitializeAsync_RunsPeriodicUpdateCheckAfterStartupUpdatePolicyDelay()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                UpdateDownloadSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.1",
                    false,
                    false,
                    "Cotton Sync is up to date.",
                    null,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.1")),
                UpdateCheckSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    false,
                    "Update 0.0.2 is available.",
                    null,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
            };
            using ManualPeriodicUpdateDelay periodicDelay = new ManualPeriodicUpdateDelay();
            using ShellViewModel viewModel = CreateViewModel(
                controller,
                checkForUpdatesOnStartup: true,
                periodicUpdateCheckInterval: TimeSpan.FromMinutes(30),
                updateDelayAsync: periodicDelay.DelayAsync);

            await viewModel.InitializeAsync();
            await viewModel.StartupUpdateTask!;
            await WaitForAsync(() => periodicDelay.RequestedDelays.Count > 0);
            periodicDelay.ReleaseNextDelay();
            await WaitForAsync(() => controller.CheckForUpdateCalls == 1);

            Assert.Multiple(() =>
            {
                Assert.That(controller.DownloadUpdateCalls, Is.EqualTo(1));
                Assert.That(controller.DownloadUpdateSources, Is.EqualTo(new[] { DesktopUpdateCheckSource.Startup }));
                Assert.That(controller.CheckForUpdateSources, Is.EqualTo(new[] { DesktopUpdateCheckSource.Periodic }));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update available"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Update 0.0.2 is available."));
                Assert.That(viewModel.CanSyncNow, Is.True);
                Assert.That(periodicDelay.RequestedDelays[0], Is.EqualTo(TimeSpan.FromMinutes(30)));
            });
        }


        [Test]
        public async Task InstallUpdateCommand_StartsDownloadedInstaller()
        {
            string installerPath = @"C:\CottonSyncUpdateCache\0.0.2\CottonSync-Windows-Setup.exe";
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                UpdateCheckSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    false,
                    "Update 0.0.2 is available.",
                    null,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                UpdateDownloadSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    true,
                    "Update 0.0.2 is ready. Click Update to install it now, or it will install automatically on next app start.",
                    installerPath,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            bool shutdownRequested = false;
            viewModel.UpdateInstallShutdownRequested += (_, _) => shutdownRequested = true;
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.CheckForUpdatesCommand);
            await ExecuteAsync(viewModel.DownloadUpdateCommand);

            await ExecuteAsync(viewModel.InstallUpdateCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.InstalledUpdatePath, Is.EqualTo(installerPath));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Installing update"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Update installer launched. Cotton Sync will restart after the update is installed."));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Installing update"));
                Assert.That(viewModel.IsUpdateInstallHandoffActive, Is.True);
                Assert.That(viewModel.IsUpdateInstallProgressVisible, Is.True);
                Assert.That(viewModel.CanInstallUpdate, Is.False);
                Assert.That(viewModel.IsUpdateInstallVisible, Is.False);
                Assert.That(viewModel.InstallUpdateCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.CanCheckForUpdates, Is.False);
                Assert.That(shutdownRequested, Is.True);
                Assert.That(viewModel.Activities.All(activity => activity.Path != installerPath), Is.True);
            });
        }


        [Test]
        public async Task InitializeAsync_WithTemporarilyUnavailableStoredSessionHidesSignInAndOffersRetry()
        {
            DesktopShellSnapshot snapshot = CreateStoredSessionWaitingSnapshot();
            FakeDesktopShellController controller = new FakeDesktopShellController(snapshot)
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://cotton.example.test/"),
                    true,
                    "Cotton Cloud",
                    "instance"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.HasStoredSession, Is.True);
                Assert.That(viewModel.IsStoredSessionRestoreVisible, Is.True);
                Assert.That(viewModel.IsServerStepVisible, Is.False);
                Assert.That(viewModel.IsSignInStepVisible, Is.False);
                Assert.That(viewModel.SetupTitle, Is.EqualTo("Reconnecting Cotton Sync"));
                Assert.That(viewModel.RetryStoredSessionCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Waiting to reconnect"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Waiting for Cotton Cloud to reconnect."));
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.StoredSessionRestoreMessage, Does.Contain("server is locked"));
            });
        }


        [Test]
        public async Task RetryStoredSessionCommand_RestoresDashboardWithoutNewSignIn()
        {
            AuthSession session = new(Guid.NewGuid(), "restored", "restored@example.test", false);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateStoredSessionWaitingSnapshot())
            {
                StoredSessionRestoreSnapshot = new DesktopStoredSessionRestoreSnapshot(session, true, null),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.RetryStoredSessionCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.RestoreStoredSessionCalls, Is.EqualTo(1));
                Assert.That(controller.RestoredSessionServerUrl, Is.EqualTo("https://cotton.example.test/"));
                Assert.That(controller.SignInRequest, Is.Null);
                Assert.That(controller.BrowserSignInServerUrl, Is.Null);
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.IsDashboardVisible, Is.True);
                Assert.That(viewModel.AccountName, Is.EqualTo("restored@example.test"));
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
            });
        }


        [Test]
        public async Task StoredSessionRetry_AutomaticallyRestoresAfterServerUnlocks()
        {
            AuthSession session = new(Guid.NewGuid(), "restored", "restored@example.test", false);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateStoredSessionWaitingSnapshot())
            {
                StoredSessionRestoreSnapshot = new DesktopStoredSessionRestoreSnapshot(session, true, null),
            };
            using ManualPeriodicUpdateDelay retryDelay = new ManualPeriodicUpdateDelay();
            using ShellViewModel viewModel = CreateViewModel(
                controller,
                storedSessionRetryInterval: TimeSpan.FromSeconds(15),
                storedSessionRetryDelayAsync: retryDelay.DelayAsync);
            await viewModel.InitializeAsync();
            await WaitForAsync(() => retryDelay.RequestedDelays.Count == 1);

            retryDelay.ReleaseNextDelay();
            await WaitForAsync(() => viewModel.IsSignedIn);

            Assert.Multiple(() =>
            {
                Assert.That(controller.RestoreStoredSessionCalls, Is.EqualTo(1));
                Assert.That(viewModel.IsDashboardVisible, Is.True);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
            });
        }


        [Test]
        public async Task InstallUpdateCommand_ShowsInstallingStateBeforeInstallerLaunchCompletes()
        {
            string installerPath = @"C:\CottonSyncUpdateCache\0.0.2\CottonSync-Windows-Setup.exe";
            TaskCompletionSource installCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                UpdateCheckSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    false,
                    "Update 0.0.2 is available.",
                    null,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                UpdateDownloadSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    true,
                    "Update 0.0.2 is ready. Click Update to install it now, or it will install automatically on next app start.",
                    installerPath,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                InstallUpdateCompletion = installCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            bool shutdownRequested = false;
            viewModel.UpdateInstallShutdownRequested += (_, _) => shutdownRequested = true;
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.CheckForUpdatesCommand);
            await ExecuteAsync(viewModel.DownloadUpdateCommand);

            viewModel.InstallUpdateCommand.Execute(null);
            await WaitForAsync(() => viewModel.UpdateDetailsText == "Starting the update installer.");

            Assert.Multiple(() =>
            {
                Assert.That(controller.InstalledUpdatePath, Is.EqualTo(installerPath));
                Assert.That(viewModel.InstallUpdateCommand.IsRunning, Is.True);
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Installing update"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Installing update"));
                Assert.That(viewModel.IsUpdateInstallProgressVisible, Is.True);
                Assert.That(viewModel.CanInstallUpdate, Is.False);
                Assert.That(viewModel.IsUpdateInstallVisible, Is.False);
                Assert.That(shutdownRequested, Is.False);
            });

            installCompletion.SetResult();
            await WaitForAsync(() => !viewModel.InstallUpdateCommand.IsRunning);
            Assert.That(shutdownRequested, Is.True);
        }
    }
}
