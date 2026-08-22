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
        public async Task RunProgressChanged_KeepsPlaceholderCreationStableBeforeFirstCreatedFile()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 17, 3, 50, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 0,
                FilesTotal: 500_000,
                CurrentPath: "Photos/2026/image-000001.jpg",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(1)));
            string withPathDetails = viewModel.CurrentWorkProgressDetails;
            bool withPathIndeterminate = viewModel.IsCurrentWorkProgressIndeterminate;
            double withPathValue = viewModel.CurrentWorkProgressValue;

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 0,
                FilesTotal: 500_000,
                CurrentPath: string.Empty,
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(withPathDetails, Is.EqualTo("Preparing cloud files \u00B7 scanning cloud \u00B7 creating placeholders \u00B7 saving state"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo(withPathDetails));
                Assert.That(withPathIndeterminate, Is.True);
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                Assert.That(withPathValue, Is.EqualTo(0));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(0));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("1 of 500,000"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("500000 cloud files queued"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.Not.Contain("cloud items queued"));
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsWhyAFullCloudPassStarted()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 100,
                FilesTotal: 500_000,
                CurrentPath: "remote-only.txt",
                StartedAtUtc: new DateTime(2026, 7, 9, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 7, 9, 9, 0, 5, DateTimeKind.Utc),
                Causes: SyncRunCause.Periodic,
                IsFull: true));

            Assert.That(
                viewModel.CurrentRunProgressDetails,
                Is.EqualTo(
                    "Making cloud files available · 100 cloud items ready · scanning cloud · saving state · Scheduled check · full folder scope"));
        }


        [Test]
        public async Task RunProgressChanged_ShowsRemoteScanCurrentPathBeforeFilesAreFound()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ScanningRemote,
                FilesCompleted: 0,
                FilesTotal: null,
                CurrentPath: "Reports",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Checking Cotton Cloud · Reports"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking Cotton Cloud · Reports"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_AggregatesMultipleFolderProgress()
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

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("8 of 30 files across 2 folders"));
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(26.666).Within(0.01));
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.False);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("8 of 30 files across 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(26.666).Within(0.01));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
            });
        }


        [Test]
        public async Task RunProgressChanged_AggregateHidesZeroOfTotalBeforeFirstCountedFile()
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
                FilesCompleted: 0,
                FilesTotal: 1494,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 15, 11, 20, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 15, 11, 20, 3, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                videosPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 506,
                CurrentPath: string.Empty,
                StartedAtUtc: new DateTime(2026, 6, 15, 11, 20, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 15, 11, 20, 4, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Preparing file checks across 2 folders"));
                Assert.That(viewModel.CurrentRunProgressDetails, Does.Not.Contain("0 of 2000"));
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Preparing file checks across 2 folders"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_AggregatesMultipleLocalScanCounts()
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
                SyncRunProgressStage.ScanningLocal,
                FilesCompleted: 123,
                FilesTotal: null,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                videosPairId,
                SyncRunProgressStage.ScanningLocal,
                FilesCompleted: 456,
                FilesTotal: null,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 6, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("579 files found across 2 folders"));
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("579 files found across 2 folders"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_AggregatesMultipleRemoteScanCounts()
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
                SyncRunProgressStage.ScanningRemote,
                FilesCompleted: 123,
                FilesTotal: null,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                videosPairId,
                SyncRunProgressStage.ScanningRemote,
                FilesCompleted: 456,
                FilesTotal: null,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 6, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("579 cloud files found across 2 folders"));
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.True);
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("579 cloud files found across 2 folders"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }
    }
}
