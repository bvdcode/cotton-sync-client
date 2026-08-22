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
        public async Task TransferProgressChanged_KeepsRunMetricsAfterCompletedSmallTransfers()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 100,
                CurrentPath: "Videos/first.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Videos/first.mp4",
                TransferredBytes: 1024,
                TotalBytes: 1024,
                IsCompleted: true,
                startedAtUtc.AddSeconds(1)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 1,
                FilesTotal: 100,
                CurrentPath: "Videos/second.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(1)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Videos/second.mp4",
                TransferredBytes: 1024,
                TotalBytes: 1024,
                IsCompleted: true,
                startedAtUtc.AddSeconds(3)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 2,
                FilesTotal: 100,
                CurrentPath: "Videos/third.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(3)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("2.0 KB"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Contain("512 B/s"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("left"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Does.Contain("2.0 KB · 512 B/s"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 2 of 100 files"));
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsGlobalFileRateWhenByteRateIsUnavailable()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0000.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 100,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0100.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.Empty);
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("10 files/s · 1m 30s left"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("10 files/s · 1m 30s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 100 of 1000 files"));
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsFolderRateForCloudFileFinalization()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 26, 3, 40, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                FilesCompleted: 0,
                FilesTotal: 8570,
                CurrentPath: "Cloud",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.FinalizingCloudFiles,
                FilesCompleted: 1400,
                FilesTotal: 8570,
                CurrentPath: "Cloud/Temp",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("140 folders/s · 55s left"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("files/s"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Finalizing cloud file status · 1400 of 8570 folders"));
            });
        }


        [Test]
        public async Task RunProgressChanged_DoesNotShowPlaceholderEtaForGrowingStreamingTotal()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 17, 4, 35, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 0,
                FilesTotal: 1000,
                CurrentPath: "Cloud/file-0000.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 100,
                FilesTotal: 1100,
                CurrentPath: "Cloud/file-0100.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("10 cloud items/s"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Making cloud files available \u00B7 100 cloud items ready \u00B7 scanning cloud \u00B7 saving state"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("of 1100"));
            });
        }


        [Test]
        public async Task RunProgressChanged_KeepsPlaceholderRowOperationStableDuringStreamingBurst()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                syncPairId,
                "Cloud",
                "Syncing",
                mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 17, 5, 30, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 120,
                FilesTotal: 10_000,
                CurrentPath: "Cloud/file-000120.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(5)));
            SyncPairRowViewModel row = viewModel.SyncPairs.Single();
            string firstOperation = row.CurrentOperation;
            string firstDetails = viewModel.CurrentWorkProgressDetails;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 260,
                FilesTotal: 10_500,
                CurrentPath: "Cloud/file-000260.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(firstOperation, Is.EqualTo("Preparing cloud files"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing cloud files"));
                Assert.That(firstDetails, Is.EqualTo("Making cloud files available \u00B7 120 cloud items ready \u00B7 scanning cloud \u00B7 saving state"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Making cloud files available \u00B7 260 cloud items ready \u00B7 scanning cloud \u00B7 saving state"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsGlobalFileRateAfterShortManyFileProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0000.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 100,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0100.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("50 files/s · 20s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 100 of 1000 files"));
            });
        }
    }
}
