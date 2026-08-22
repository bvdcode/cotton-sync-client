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
        public async Task SessionRevoked_DuringTransferClearsProgressAndReturnsToRecoverableSignIn()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 7, 17, 18, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.HydratingCloudFiles,
                FilesCompleted: 4,
                FilesTotal: 20,
                CurrentPath: "Music/track-005.flac",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(2),
                BytesCompleted: 8 * 1024 * 1024,
                BytesTotal: 40 * 1024 * 1024));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Music/track-005.flac",
                TransferredBytes: 2 * 1024 * 1024,
                TotalBytes: 8 * 1024 * 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(3)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.True);
            });

            viewModel.Password = "password";
            viewModel.TotpCode = "123456";
            controller.ReportSessionRevoked(new DesktopSessionRevocationSnapshot(startedAtUtc.AddSeconds(4)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.IsSetupVisible, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Session expired"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sign in to start sync."));
                Assert.That(viewModel.Password, Is.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.CurrentTransferTitle, Is.Empty);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.Empty);
                Assert.That(row.Status, Is.EqualTo("Idle"));
                Assert.That(row.HasCurrentProgress, Is.False);
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Session expired"));
            });
        }


        [Test]
        public async Task FutureSyncModesVisibility_UsesDefaultOnFeatureFlagAndCloudFilesCapability()
        {
            using ShellViewModel defaultViewModel = CreateViewModel(
                new FakeDesktopShellController(CreateSignedInSnapshot(platformCapabilities: CreatePlatformCapabilities(windowsVirtualFilesSupported: true))));
            using ShellViewModel explicitlyHiddenViewModel = CreateViewModel(
                new FakeDesktopShellController(CreateSignedInSnapshot(platformCapabilities: CreatePlatformCapabilities(windowsVirtualFilesSupported: true))),
                new DesktopFeatureFlags(false));
            using ShellViewModel visibleViewModel = CreateViewModel(
                new FakeDesktopShellController(CreateSignedInSnapshot(platformCapabilities: CreatePlatformCapabilities(windowsVirtualFilesSupported: true))),
                new DesktopFeatureFlags(true));
            using ShellViewModel unsupportedViewModel = CreateViewModel(
                new FakeDesktopShellController(CreateSignedInSnapshot(platformCapabilities: CreatePlatformCapabilities(windowsVirtualFilesSupported: false))),
                new DesktopFeatureFlags(true));

            await defaultViewModel.InitializeAsync();
            await explicitlyHiddenViewModel.InitializeAsync();
            await visibleViewModel.InitializeAsync();
            await unsupportedViewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(defaultViewModel.IsFutureSyncModesVisible, Is.True);
                Assert.That(explicitlyHiddenViewModel.IsFutureSyncModesVisible, Is.False);
                Assert.That(visibleViewModel.IsFutureSyncModesVisible, Is.True);
                Assert.That(unsupportedViewModel.IsFutureSyncModesVisible, Is.False);
                Assert.That(visibleViewModel.SelectedSyncModeLabel, Is.EqualTo("Full mirror"));
            });
        }
    }
}
