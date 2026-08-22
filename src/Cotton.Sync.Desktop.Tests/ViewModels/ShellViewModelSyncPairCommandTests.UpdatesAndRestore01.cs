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
        public void AppVersion_UsesInformationalVersionWithoutBuildMetadata()
        {
            using ShellViewModel viewModel = CreateViewModel(new FakeDesktopShellController(CreateSignedOutSnapshot()));
            Assert.That(viewModel.AppVersion, Is.EqualTo(DesktopProductVersion.Current));
        }


        [Test]
        public async Task CheckForUpdatesCommand_ShowsAvailableUpdateWithoutBlockingSyncCommands()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                UpdateCheckSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.1",
                    "0.0.2",
                    true,
                    false,
                    "Update 0.0.2 is available.",
                    null,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.CheckForUpdateCalls, Is.EqualTo(1));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update available"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Update 0.0.2 is available."));
                Assert.That(viewModel.IsUpdateAvailable, Is.True);
                Assert.That(viewModel.IsUpdateReady, Is.False);
                Assert.That(viewModel.CanSyncNow, Is.True);
            });
        }


        [Test]
        public async Task DownloadUpdateCommand_MarksUpdateReadyForInstallNowOrNextStartup()
        {
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
                    @"C:\Users\qa\AppData\Roaming\Cotton\Sync\updates\0.0.2\CottonSync-Windows-Setup.exe",
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            await ExecuteAsync(viewModel.DownloadUpdateCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.DownloadUpdateCalls, Is.EqualTo(1));
                Assert.That(controller.DownloadUpdateSources, Is.EqualTo(new[] { DesktopUpdateCheckSource.Download }));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update ready"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Update ready"));
                Assert.That(viewModel.IsUpdateReady, Is.True);
                Assert.That(viewModel.CanInstallUpdate, Is.True);
                Assert.That(viewModel.IsUpdateDownloadVisible, Is.False);
            });
        }


        [Test]
        public async Task DownloadUpdateCommand_ShowsDownloadProgressUntilInstallerIsReady()
        {
            TaskCompletionSource<DesktopUpdateStatusSnapshot> downloadCompletion = new TaskCompletionSource<DesktopUpdateStatusSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
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
                    @"C:\Users\qa\AppData\Roaming\Cotton\Sync\updates\0.0.2\CottonSync-Windows-Setup.exe",
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                UpdateDownloadCompletion = downloadCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            Assert.That(viewModel.DownloadUpdateCommand.CanExecute(null), Is.True);
            viewModel.DownloadUpdateCommand.Execute(null);
            await WaitForAsync(() => controller.DownloadProgressReports.Count == 2);
            await WaitForAsync(() => viewModel.UpdateDetailsText.Contains("100%", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.DownloadUpdateCommand.IsRunning, Is.True);
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Downloading update"));
                Assert.That(viewModel.UpdateDetailsText, Does.Contain("1.0 KB / 1.0 KB"));
                Assert.That(viewModel.UpdateDetailsText, Does.Contain("100%"));
                Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.True);
                Assert.That(viewModel.IsUpdateDownloadProgressIndeterminate, Is.False);
                Assert.That(viewModel.UpdateDownloadProgressValue, Is.EqualTo(100d));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Downloading update"));
                Assert.That(viewModel.CanCheckForUpdates, Is.False);
                Assert.That(viewModel.CanDownloadUpdate, Is.False);
                Assert.That(viewModel.IsUpdateDownloadVisible, Is.False);
            });

            downloadCompletion.SetResult(controller.UpdateDownloadSnapshot!);
            await WaitForAsync(() => !viewModel.DownloadUpdateCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update ready"));
                Assert.That(viewModel.IsUpdateReady, Is.True);
                Assert.That(viewModel.CanInstallUpdate, Is.True);
                Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.False);
            });
        }


        [Test]
        public async Task DownloadUpdateCommand_ShowsPreparingStateBeforeFirstProgress()
        {
            TaskCompletionSource<DesktopUpdateStatusSnapshot> downloadCompletion = new TaskCompletionSource<DesktopUpdateStatusSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
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
                    @"C:\Users\qa\AppData\Roaming\Cotton\Sync\updates\0.0.2\CottonSync-Windows-Setup.exe",
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                UpdateDownloadCompletion = downloadCompletion,
                SuppressDownloadProgress = true,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            viewModel.DownloadUpdateCommand.Execute(null);
            await WaitForAsync(() => viewModel.UpdateStatusText == "Downloading update");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.DownloadUpdateCommand.IsRunning, Is.True);
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Preparing update download."));
                Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.True);
                Assert.That(viewModel.IsUpdateDownloadProgressIndeterminate, Is.True);
                Assert.That(viewModel.UpdateDownloadProgressValue, Is.EqualTo(0d));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Downloading update"));
                Assert.That(viewModel.CanCheckForUpdates, Is.False);
                Assert.That(viewModel.CanDownloadUpdate, Is.False);
                Assert.That(viewModel.IsUpdateDownloadVisible, Is.False);
            });

            downloadCompletion.SetResult(controller.UpdateDownloadSnapshot!);
            await WaitForAsync(() => !viewModel.DownloadUpdateCommand.IsRunning);
            Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.False);
        }


        [Test]
        public async Task InitializeAsync_AutoDownloadsUpdateOnStartupWithoutBlockingSyncCommands()
        {
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
            };
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(
                controller,
                notificationService: notificationService,
                checkForUpdatesOnStartup: true);

            await viewModel.InitializeAsync();
            await viewModel.StartupUpdateTask!;

            Assert.Multiple(() =>
            {
                Assert.That(controller.DownloadUpdateCalls, Is.EqualTo(1));
                Assert.That(controller.DownloadUpdateSources, Is.EqualTo(new[] { DesktopUpdateCheckSource.Startup }));
                Assert.That(controller.CheckForUpdateCalls, Is.EqualTo(0));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update ready"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Update ready"));
                Assert.That(viewModel.IsUpdateReady, Is.True);
                Assert.That(viewModel.CanInstallUpdate, Is.True);
                Assert.That(viewModel.CanSyncNow, Is.True);
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Update ready"));
            });
        }


        [Test]
        public async Task InitializeAsync_WhenStartupUpdateIsCurrentDoesNotShowDownloadProgress()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                UpdateDownloadSnapshot = new DesktopUpdateStatusSnapshot(
                    "0.0.2",
                    "0.0.2",
                    false,
                    false,
                    "Cotton Sync is up to date.",
                    null,
                    new Uri("https://github.com/bvdcode/cotton-sync-client/releases/tag/v0.0.2")),
                SuppressDownloadProgress = true,
            };
            using ShellViewModel viewModel = CreateViewModel(controller, checkForUpdatesOnStartup: true);

            await viewModel.InitializeAsync();
            await viewModel.StartupUpdateTask!;

            Assert.Multiple(() =>
            {
                Assert.That(controller.DownloadUpdateCalls, Is.EqualTo(1));
                Assert.That(controller.DownloadUpdateSources, Is.EqualTo(new[] { DesktopUpdateCheckSource.Startup }));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Up to date"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Cotton Sync is up to date."));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
                Assert.That(viewModel.IsUpdateReady, Is.False);
                Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.False);
                Assert.That(viewModel.IsUpdateDownloadProgressIndeterminate, Is.False);
                Assert.That(viewModel.UpdateDownloadProgressValue, Is.EqualTo(0d));
            });
        }
    }
}
