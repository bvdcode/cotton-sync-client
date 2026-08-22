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
        public async Task RunProgressChanged_ClearsCompletedRunProgressBeforeIdleStatusArrives()
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

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.Completed,
                FilesCompleted: 10,
                FilesTotal: 10,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: true,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 15, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.Empty);
                Assert.That(viewModel.CurrentRunProgressDetails, Is.Empty);
                Assert.That(row.CurrentOperation, Is.Empty);
                Assert.That(row.HasCurrentOperation, Is.False);
                Assert.That(row.HasCurrentProgress, Is.False);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
                Assert.That(row.CurrentProgressValue, Is.Zero);
            });
        }


        [Test]
        public async Task RunProgressChanged_RemovesCompletedFolderFromAggregateProgress()
        {
            Guid documentsPairId = Guid.NewGuid();
            Guid videosPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(documentsPairId, "Documents", "Syncing"),
                    CreatePair(videosPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                documentsPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 5, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                videosPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 5,
                FilesTotal: 20,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 6, DateTimeKind.Utc)));

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                documentsPairId,
                SyncRunProgressStage.Completed,
                FilesCompleted: 10,
                FilesTotal: 10,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: true,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 15, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel documentsRow = viewModel.SyncPairs.Single(pair => pair.Id == documentsPairId);
                SyncPairRowViewModel videosRow = viewModel.SyncPairs.Single(pair => pair.Id == videosPairId);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Videos"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 5 of 20 files"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(25).Within(0.01));
                Assert.That(documentsRow.HasCurrentProgress, Is.False);
                Assert.That(documentsRow.CurrentOperation, Is.Empty);
                Assert.That(videosRow.HasCurrentProgress, Is.True);
                Assert.That(videosRow.CurrentOperation, Is.EqualTo("Checking files 5 of 20"));
            });
        }


        [Test]
        public async Task TransferProgressChanged_KeepsAggregateRunProgressPrimaryForMultipleFolders()
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
                new DateTime(2026, 6, 4, 9, 0, 7, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("512 B · 1.3 files/s · 20s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("8 of 30 files across 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(28.333).Within(0.01));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
            });
        }


        [Test]
        public async Task TransferProgressChanged_KeepsRunProgressPrimaryForOneFolder()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 20,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 5,
                FilesTotal: 20,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 5, DateTimeKind.Utc)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 7, DateTimeKind.Utc),
                SpeedBytesPerSecond: 256,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressHeaderDetails, Is.EqualTo("512 B · 256 B/s · 15s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 5 of 20 files"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(27.5).Within(0.01));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
            });
        }


        [Test]
        public async Task TransferProgressChanged_KeepsAvailabilityRunProgressPrimaryForOneFolder()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Music", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.HydratingCloudFiles,
                FilesCompleted: 100,
                FilesTotal: 1000,
                CurrentPath: "Albums/Track 101.flac",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10),
                Causes: SyncRunCause.LocalChange,
                RequestedPathCount: 1));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Albums/Track 101.flac",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(11),
                SpeedBytesPerSecond: 256,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Music"));
                Assert.That(
                    viewModel.CurrentWorkProgressDetails,
                    Is.EqualTo("Making files available · 100 of 1000 files · Local change · 1 changed path"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("Track 101.flac"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
                Assert.That(viewModel.CurrentTrayActivityKind, Is.EqualTo(DesktopTrayActivityKind.MakingAvailable));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(10.05).Within(0.01));
                Assert.That(viewModel.CurrentTransferProgressValue, Is.EqualTo(50).Within(0.01));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(row.CurrentOperation, Is.EqualTo("Downloading Track 101.flac"));
                Assert.That(row.CurrentProgressValue, Is.EqualTo(10.05).Within(0.01));
            });
        }
    }
}
