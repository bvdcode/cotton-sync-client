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
        public async Task ApplyVisualSmokeScenarioAsync_ShowsManySmallDownloadProgress()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Syncing"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.ManySmallDownload);

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.First();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 410 of 500 files"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.EqualTo("Processing queued changes"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(82.25).Within(0.01));
                Assert.That(row.CurrentOperation, Is.EqualTo("Downloading 2 files"));
                Assert.That(row.HasCurrentProgress, Is.True);
                Assert.That(row.CurrentProgressValue, Is.EqualTo(82.25).Within(0.01));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsHighPressureStartingWithoutZeroCounter()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Syncing"),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.HighPressureStarting);

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.First();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Preparing file checks · 1494 files queued"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("0 of 1494"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing file checks"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Preparing file checks"));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsVirtualFilesSeedingWithoutQueuedOrDownloadCopy()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles),
                    CreatePair(Guid.NewGuid(), "Camera uploads", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.VirtualFilesSeeding);

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.First();
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Cloud"));
                Assert.That(
                    viewModel.CurrentWorkProgressDetails,
                    Is.EqualTo("Making cloud files available \u00B7 118054 cloud items ready \u00B7 scanning cloud \u00B7 saving state"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("left"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentWorkProgressAutomationName, Is.EqualTo("Open-ended cloud file progress"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing cloud files"));
                Assert.That(row.CurrentProgressAutomationName, Is.EqualTo("Open-ended cloud file progress"));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsUpdateDownloadProgress()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.UpdateDownloadProgress);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Downloading update"));
                Assert.That(viewModel.UpdateDetailsText, Does.Contain("24 MB / 96 MB"));
                Assert.That(viewModel.UpdateDetailsText, Does.Contain("25%"));
                Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.True);
                Assert.That(viewModel.IsUpdateDownloadProgressIndeterminate, Is.False);
                Assert.That(viewModel.UpdateDownloadProgressValue, Is.EqualTo(25d));
                Assert.That(viewModel.IsUpdateDownloadVisible, Is.False);
                Assert.That(viewModel.IsSettingsVisible, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Downloading update"));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsUpdateInstallProgress()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.UpdateInstallProgress);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.UpdateStatusText, Is.EqualTo("Installing update"));
                Assert.That(viewModel.UpdateDetailsText, Is.EqualTo("Starting the update installer."));
                Assert.That(viewModel.IsUpdateInstallProgressVisible, Is.True);
                Assert.That(viewModel.IsUpdateDownloadProgressVisible, Is.False);
                Assert.That(viewModel.IsUpdateInstallVisible, Is.False);
                Assert.That(viewModel.IsSettingsVisible, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Installing update"));
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsFolderControls()
        {
            Guid firstPairId = Guid.NewGuid();
            Guid secondPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(firstPairId, "Documents", "Idle"),
                    CreatePair(secondPairId, "Photos", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.FolderControls);

            SyncPairRowViewModel firstPair = viewModel.SyncPairs.Single(pair => pair.Id == firstPairId);
            SyncPairRowViewModel secondPair = viewModel.SyncPairs.Single(pair => pair.Id == secondPairId);
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSelectedSyncPairEditorVisible, Is.True);
                Assert.That(viewModel.SelectedSyncPair?.Id, Is.EqualTo(firstPairId));
                Assert.That(firstPair.IsEditorVisible, Is.True);
                Assert.That(secondPair.IsEditorVisible, Is.False);
                Assert.That(viewModel.IsRemoveSyncPairConfirmationVisible, Is.False);
            });
        }


        [Test]
        public async Task ApplyVisualSmokeScenarioAsync_ShowsConflictList()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await viewModel.ApplyVisualSmokeScenarioAsync(DesktopVisualSmokeScenario.Conflict);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasConflicts, Is.True);
                Assert.That(viewModel.ConflictCountLabel, Is.EqualTo("1 conflict"));
                Assert.That(viewModel.SelectedConflict?.Path, Is.EqualTo("Reports/budget.xlsx"));
                Assert.That(viewModel.Activities.First().Kind, Is.EqualTo("Conflict"));
            });
        }
    }
}
