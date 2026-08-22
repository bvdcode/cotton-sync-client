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
        public async Task RunProgressChanged_UpdatesCurrentRunProgressState()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.False);
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Checking files · 3 of 10 files"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 3 of 10 files"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Checking files 3 of 10"));
                Assert.That(row.HasCurrentOperation, Is.True);
                Assert.That(row.HasCurrentProgress, Is.True);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
                Assert.That(row.CurrentProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Checking files 3 of 10"));
            });
        }


        [Test]
        public async Task RunProgressChanged_UpdatesPlaceholderCreationProgressState()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "remote-only.txt",
                StartedAtUtc: new DateTime(2026, 6, 16, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 16, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Making cloud files available \u00B7 3 cloud items ready \u00B7 scanning cloud \u00B7 saving state"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Making cloud files available \u00B7 3 cloud items ready \u00B7 scanning cloud \u00B7 saving state"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing cloud files"));
                Assert.That(row.HasCurrentOperation, Is.True);
                Assert.That(row.HasCurrentProgress, Is.True);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.True);
                Assert.That(row.CurrentProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Preparing cloud files"));
            });
        }


        [Test]
        public async Task RunProgressChanged_HidesZeroOfTotalBeforeFirstCountedFile()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1494,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 15, 11, 20, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 15, 11, 20, 3, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Preparing file checks · 1494 files queued"));
                Assert.That(viewModel.CurrentRunProgressDetails, Does.Not.Contain("0 of 1494"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("0 of 1494"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing file checks"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Preparing file checks"));
            });
        }


        [Test]
        public async Task RunProgressChanged_DoesNotFlickerBackToZeroWhenCurrentPathDropsDuringPressure()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 15, 11, 20, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1494,
                CurrentPath: "moved-00001.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(3)));
            string withPathDetails = viewModel.CurrentWorkProgressDetails;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1494,
                CurrentPath: string.Empty,
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(4)));

            Assert.Multiple(() =>
            {
                Assert.That(withPathDetails, Is.EqualTo("Checking files · 1 of 1494 files"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Preparing file checks · 1494 files queued"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("0 of 1494"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_UsesPlannedBytesForGlobalProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            const long totalBytes = 10L * 1024 * 1024 * 1024;
            const long completedBytes = 3L * 1024 * 1024 * 1024;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 200,
                FilesTotal: 29189,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 7, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 7, 9, 1, 0, DateTimeKind.Utc),
                BytesCompleted: completedBytes,
                BytesTotal: totalBytes));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("3.0 GB / 10 GB"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 200 of 29189 files"));
            });
        }


        [Test]
        public async Task RunProgressChanged_ManySmallDownloadCounterMovesForward()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 15, 11, 5, 0, DateTimeKind.Utc);
            const long fileSize = 4096;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 325,
                FilesTotal: 500,
                CurrentPath: "Downloads/small-files/batch-0325.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(18),
                BytesCompleted: 325 * fileSize,
                BytesTotal: 500 * fileSize));
            double firstProgress = viewModel.CurrentWorkProgressValue;
            string firstDetails = viewModel.CurrentWorkProgressDetails;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 410,
                FilesTotal: 500,
                CurrentPath: "Downloads/small-files/batch-0410.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(19),
                BytesCompleted: 410 * fileSize,
                BytesTotal: 500 * fileSize));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(firstDetails, Is.EqualTo("Checking files · 325 of 500 files"));
                Assert.That(firstProgress, Is.EqualTo(65).Within(0.01));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 410 of 500 files"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.EqualTo("Processing queued changes"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(82).Within(0.01));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.GreaterThan(firstProgress));
                Assert.That(row.CurrentOperation, Is.EqualTo("Checking files 410 of 500"));
            });
        }


        [Test]
        public async Task RunProgressChanged_KeepsQueuedWorkIndicatorOffForSmallBatches()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 5,
                FilesTotal: 20,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 15, 11, 5, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 15, 11, 5, 1, DateTimeKind.Utc)));

            Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
        }


        [Test]
        public async Task RunProgressChanged_UsesGlobalBytesForHeaderSpeedAndEstimate()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 7, 9, 0, 0, DateTimeKind.Utc);
            const long totalBytes = 10L * 1024 * 1024 * 1024;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 100,
                FilesTotal: 1_000,
                CurrentPath: "Videos/clip-100.mp4",
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc.AddSeconds(10),
                BytesCompleted: 1L * 1024 * 1024 * 1024,
                BytesTotal: totalBytes));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 200,
                FilesTotal: 1_000,
                CurrentPath: "Videos/clip-200.mp4",
                startedAtUtc,
                IsCompleted: false,
                startedAtUtc.AddSeconds(15),
                BytesCompleted: 2L * 1024 * 1024 * 1024,
                BytesTotal: totalBytes));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("2.0 GB / 10 GB"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("205 MB/s · 40s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 200 of 1000 files"));
            });
        }
    }
}
