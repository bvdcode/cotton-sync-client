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
        public async Task InstallUpdateCommand_ShowsFailureWhenInstallerLaunchFails()
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
                InstallUpdateException = new InvalidOperationException("Cotton Sync update installer could not be started."),
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
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update failed"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Cotton Sync update installer could not be started."));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Update failed"));
                Assert.That(viewModel.IsUpdateInstallHandoffActive, Is.False);
                Assert.That(viewModel.IsUpdateInstallProgressVisible, Is.False);
                Assert.That(viewModel.CanInstallUpdate, Is.True);
                Assert.That(viewModel.IsUpdateInstallVisible, Is.True);
                Assert.That(viewModel.InstallUpdateCommand.CanExecute(null), Is.True);
                Assert.That(shutdownRequested, Is.False);
            });
        }


        [Test]
        public async Task CheckForUpdatesCommand_ShowsRetryableNetworkFailure()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                UpdateCheckException = new HttpRequestException("firewall denied first request"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update failed"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Cannot reach update server. Check network or firewall and retry."));
                Assert.That(viewModel.CanCheckForUpdates, Is.True);
                Assert.That(viewModel.CanSyncNow, Is.True);
            });
        }


        [Test]
        public async Task InitializeAsync_WhenStartupUpdateFailsShowsRetryableStatusWithoutOverridingSyncStatus()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                UpdateDownloadException = new HttpRequestException("firewall denied first request"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller, checkForUpdatesOnStartup: true);

            await viewModel.InitializeAsync();
            await viewModel.StartupUpdateTask!;

            Assert.Multiple(() =>
            {
                Assert.That(controller.DownloadUpdateCalls, Is.EqualTo(1));
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update failed"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Cannot reach update server. Check network or firewall and retry."));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
                Assert.That(viewModel.CanSyncNow, Is.True);
                Assert.That(viewModel.CanCheckForUpdates, Is.True);
            });
        }


        [Test]
        public async Task CheckForUpdatesCommand_ShowsPublishingRaceForNotFound()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot())
            {
                UpdateCheckException = new HttpRequestException(
                    "not found",
                    null,
                    HttpStatusCode.NotFound),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update failed"));
                Assert.That(
                    viewModel.UpdateDetailsText,
                    Is.EqualTo("Update metadata or installer was not found. Retry after the release finishes publishing."));
                Assert.That(viewModel.CanCheckForUpdates, Is.True);
            });
        }


        [Test]
        public async Task DownloadUpdateCommand_ShowsRetryableHashMismatchAndKeepsDownloadAvailable()
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
                UpdateDownloadException = new InvalidDataException("Downloaded update SHA-256 does not match release manifest."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await ExecuteAsync(viewModel.CheckForUpdatesCommand);

            await ExecuteAsync(viewModel.DownloadUpdateCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Update failed"));
                Assert.That(
                    viewModel.UpdateDetailsText,
                    Is.EqualTo("Downloaded update failed integrity verification. Delete the cached update and retry download."));
                Assert.That(viewModel.IsUpdateAvailable, Is.True);
                Assert.That(viewModel.CanDownloadUpdate, Is.True);
            });
        }
    }
}
