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
        public async Task TransferProgressChanged_KeepsGlobalRunByteProgressPrimaryForOneFolder()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 7, 9, 0, 0, DateTimeKind.Utc);
            const long totalRunBytes = 10L * 1024 * 1024 * 1024;
            const long completedRunBytes = 3L * 1024 * 1024 * 1024;
            const long currentFileTransferredBytes = 512L * 1024 * 1024;
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 200,
                FilesTotal: 29189,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(60),
                BytesCompleted: completedRunBytes,
                BytesTotal: totalRunBytes));

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 256L * 1024 * 1024,
                TotalBytes: 1024L * 1024 * 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(60),
                SpeedBytesPerSecond: 512L * 1024 * 1024,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(2)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: currentFileTransferredBytes,
                TotalBytes: 1024L * 1024 * 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(62),
                SpeedBytesPerSecond: 64L * 1024 * 1024,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(8)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Videos"));
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("3.5 GB / 10 GB"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("128 MB/s · 55s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 200 of 29189 files"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.EqualTo("Processing queued changes"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(35).Within(0.01));
            });
        }


        [Test]
        public async Task TransferProgressChanged_AggregatesHeaderMetricsForMultipleActiveTransfers()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = CreateTwoFolderSyncingController(documentsPairId, videosPairId);
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            ReportTwoFolderCheckingProgress(controller, documentsPairId, videosPairId);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                documentsPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 7, DateTimeKind.Utc),
                SpeedBytesPerSecond: 256));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                videosPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 1536,
                TotalBytes: 3072,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 8, DateTimeKind.Utc),
                SpeedBytesPerSecond: 512));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("2.0 KB · 768 B/s · 20s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("8 of 30 files across 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(30).Within(0.01));
            });
        }


        [TestCase(SyncTransferDirection.Download, "Downloading")]
        [TestCase(SyncTransferDirection.Upload, "Uploading")]
        public async Task TransferProgressChanged_AggregatesConcurrentFilesWithinOneFolder(
            SyncTransferDirection direction,
            string action)
        {
            DesktopTrayActivityKind expectedTrayActivityKind = direction switch
            {
                SyncTransferDirection.Download => DesktopTrayActivityKind.Downloading,
                SyncTransferDirection.Upload => DesktopTrayActivityKind.Uploading,
                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unexpected test direction."),
            };
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Music", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 8, 4, 3, 0, 0, DateTimeKind.Utc);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                direction,
                "Albums/first.flac",
                TransferredBytes: 25,
                TotalBytes: 100,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc,
                SpeedBytesPerSecond: 10,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(8)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                direction,
                "Albums/second.flac",
                TransferredBytes: 50,
                TotalBytes: 100,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(1),
                SpeedBytesPerSecond: 20,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(3)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo($"Music: {action} 2 files"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("75 B / 200 B · 30 B/s · 8s left"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(37.5).Within(0.01));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
                Assert.That(viewModel.CurrentTrayActivityKind, Is.EqualTo(expectedTrayActivityKind));
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(row.CurrentOperation, Is.EqualTo($"{action} 2 files"));
                Assert.That(row.CurrentProgressValue, Is.EqualTo(37.5).Within(0.01));
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
            });

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                direction,
                "Albums/second.flac",
                TransferredBytes: 100,
                TotalBytes: 100,
                IsCompleted: true,
                OccurredAtUtc: startedAtUtc.AddSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo($"Music: {action} first.flac"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("25 B / 100 B · 10 B/s · 8s left"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(25).Within(0.01));
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(row.CurrentOperation, Is.EqualTo($"{action} first.flac"));
                Assert.That(row.CurrentProgressValue, Is.EqualTo(25).Within(0.01));
            });
        }


        [Test]
        public async Task TransferProgressChanged_OmitsTransferEstimateFromAggregateRunHeader()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = CreateTwoFolderSyncingController(documentsPairId, videosPairId);
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            ReportTwoFolderCheckingProgress(controller, documentsPairId, videosPairId);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                documentsPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 7, DateTimeKind.Utc),
                SpeedBytesPerSecond: 256,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(2)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                videosPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 1536,
                TotalBytes: 3072,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 8, DateTimeKind.Utc),
                SpeedBytesPerSecond: 512,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(20)));

            Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("2.0 KB · 768 B/s · 20s left"));
        }


        [Test]
        public async Task TransferProgressChanged_DoesNotDuplicateAggregateRunDetailsAfterTransferCompletes()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = CreateTwoFolderSyncingController(documentsPairId, videosPairId);
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            ReportTwoFolderCheckingProgress(controller, documentsPairId, videosPairId);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                documentsPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 1024,
                TotalBytes: 1024,
                IsCompleted: true,
                new DateTime(2026, 6, 4, 9, 0, 7, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("1.0 KB · 1.3 files/s · 20s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("8 of 30 files across 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
            });
        }


        [Test]
        public async Task TransferProgressChanged_DoesNotCountUntransferredBytesOnTerminalSample()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 7, 25, 20, 46, 18, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 10,
                FilesTotal: 100,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 256,
                TotalBytes: 1024,
                IsCompleted: false,
                startedAtUtc.AddSeconds(1)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 256,
                TotalBytes: 1024,
                IsCompleted: true,
                startedAtUtc.AddSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("256 B"));
                Assert.That(viewModel.SyncPairs.Single().CurrentOperation, Is.EqualTo("Checking files 10 of 100"));
            });
        }
    }
}
