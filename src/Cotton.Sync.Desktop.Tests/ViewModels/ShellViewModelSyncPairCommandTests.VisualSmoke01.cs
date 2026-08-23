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
        public async Task ApplyVisualSmokeScenarioAsync_ShowsSettings()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.Settings);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSettingsVisible, Is.True);
                Assert.That(viewModel.IsDashboardChromeVisible, Is.False);
                Assert.That(viewModel.SelectedSettingsTabIndex, Is.EqualTo(0));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsConnectingStartupState()
        {
            using ShellViewModel viewModel = CreateViewModel(new FakeDesktopShellController(CreateSignedOutSnapshot()));
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.Connecting);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsStartupLoadingVisible, Is.True);
                Assert.That(viewModel.IsSetupVisible, Is.False);
                Assert.That(viewModel.IsDashboardVisible, Is.False);
                Assert.That(viewModel.IsDashboardHeaderVisible, Is.False);
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsSignInError()
        {
            using ShellViewModel viewModel = CreateViewModel(new FakeDesktopShellController(CreateSignedOutSnapshot()));
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.SignInError);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSetupVisible, Is.True);
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.ServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
                Assert.That(viewModel.Username, Is.EqualTo("qa@cottoncloud.dev"));
                Assert.That(viewModel.Password, Is.Not.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sign-in failed"));
                Assert.That(viewModel.HasActionRequired, Is.True);
                Assert.That(viewModel.CanRetryActionRequired, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Invalid username or password."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sign in to continue."));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsAddFolderWizard()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Photos", "/Photos"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.AddFolder);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(0));
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairLocalStepVisible, Is.False);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.True);
                Assert.That(viewModel.IsWindowsVirtualFilesSupported, Is.True);
                Assert.That(viewModel.IsFutureSyncModesVisible, Is.True);
                Assert.That(viewModel.LocalFolderPath, Is.Not.Empty);
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolders.Select(static folder => folder.Name), Is.EqualTo(new[] { "Documents", "Photos" }));
                Assert.That(viewModel.SelectedRemoteFolder, Is.Null);
                Assert.That(controller.ListRemoteFolderPaths, Is.EqualTo(new[] { "/" }));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsManyRemoteFolders()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                Enumerable.Range(1, 250)
                    .Select(index => new DesktopRemoteFolderSnapshot(
                        Guid.NewGuid(),
                        "Project archive " + index.ToString("000", CultureInfo.InvariantCulture),
                        "/Project archive " + index.ToString("000", CultureInfo.InvariantCulture)))
                    .ToArray());
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.AddFolderManyRemoteFolders);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(0));
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.True);
                Assert.That(viewModel.IsAddSyncPairCloudStepVisible, Is.True);
                Assert.That(viewModel.RemoteFolders, Has.Count.EqualTo(250));
                Assert.That(viewModel.RemoteFolders.First().Name, Is.EqualTo("Project archive 001"));
                Assert.That(viewModel.RemoteFolders.Last().Name, Is.EqualTo("Project archive 250"));
                Assert.That(controller.ListRemoteFolderPaths, Is.EqualTo(new[] { "/" }));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsEmptyDashboardWithoutOpeningWizard()
        {
            FakeLocalFolderPicker localFolderPicker = new FakeLocalFolderPicker();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller, localFolderPicker: localFolderPicker);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.EmptyDashboard);

            Assert.Multiple(() =>
            {
                Assert.That(localFolderPicker.PickFolderCalls, Is.EqualTo(0));
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.SyncPairs, Is.Empty);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.Empty);
                Assert.That(viewModel.HeaderTitleText, Is.EqualTo("vadim@example.com"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Connected"));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsSettingsDiagnosticsTab()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                SelfTestSnapshot = new DesktopSelfTestSnapshot(
                [
                    new DesktopSelfTestItemSnapshot("Preferences database", true, "Writable"),
                    new DesktopSelfTestItemSnapshot("Token storage", true, "Release-secure storage available"),
                ]),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.SettingsDiagnostics);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSettingsVisible, Is.True);
                Assert.That(viewModel.IsDashboardChromeVisible, Is.False);
                Assert.That(viewModel.SelectedSettingsTabIndex, Is.EqualTo(2));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Diagnostics exported"));
                Assert.That(viewModel.HasSelfTestItems, Is.True);
                Assert.That(viewModel.SelfTestItems, Has.Count.EqualTo(2));
                Assert.That(viewModel.HasLastDiagnosticsBundlePath, Is.True);
                Assert.That(viewModel.LastDiagnosticsBundlePath, Is.EqualTo(controller.ExportDiagnosticsPath));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsActionRequiredError()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.Error);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("Cotton Cloud desktop change feed is unavailable. Check the server deployment; Cotton Sync will retry automatically."));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsMissingLocalRootError()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.MissingLocalRoot);

            const string message =
                "Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync.";
            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Fix the issue below to continue syncing."));
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo(message));
                Assert.That(row.Status, Is.EqualTo("Error"));
                Assert.That(row.LastError, Is.EqualTo(message));
                Assert.That(viewModel.Activities.First().Details, Is.EqualTo(message));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsOfflineState()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Idle"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.Offline);

            const string message = "Cannot reach Cotton Cloud. Sync will retry automatically.";
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Offline"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Offline"));
                Assert.That(viewModel.StatusCardTitle, Is.EqualTo("Offline"));
                Assert.That(viewModel.HasOfflineStatus, Is.True);
                Assert.That(viewModel.HasStatusAttention, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Waiting for connection to recover."));
                Assert.That(viewModel.SyncPairs.Select(static row => row.Status), Is.All.EqualTo("Offline"));
                Assert.That(viewModel.SyncPairs.Select(static row => row.LastError), Is.All.EqualTo(message));
                Assert.That(viewModel.Activities.First().Details, Is.EqualTo(message));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsProgressCards()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Syncing"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.Progress);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("10 of 40 files across 2 folders"));
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.CurrentTransferTitle, Is.EqualTo("Camera uploads: Downloading 07.7z"));
                Assert.That(viewModel.CurrentTransferDetails, Does.Contain("/s"));
                Assert.That(viewModel.CurrentTransferDetails, Does.Contain("left"));
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("10 MB · 5.0 MB/s"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("10 of 40 files across 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.SyncPairs.First().CurrentOperation, Is.EqualTo("Uploading 2 files"));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsLongProgressFileName()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Syncing"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.LongProgress);

            const string expectedFileName =
                "quarterly-budget-with-a-very-long-file-name-that-should-stay-ellipsized-in-active-progress-final-approved-upload-copy-2026-06-15.xlsx";
            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.First();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Contain("17 of 42 files"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Uploading " + expectedFileName));
                Assert.That(row.HasCurrentOperation, Is.True);
                Assert.That(row.HasCurrentProgress, Is.True);
            });
        }
    }
}
