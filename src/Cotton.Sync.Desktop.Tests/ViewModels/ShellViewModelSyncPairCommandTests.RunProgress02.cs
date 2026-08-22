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
        public async Task RunProgressChanged_ThrottlesVisibleUpdates()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 1,
                FilesTotal: 100,
                CurrentPath: "Reports/file-001.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 50,
                FilesTotal: 100,
                CurrentPath: "Reports/file-050.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMilliseconds(50)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(1).Within(0.01));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Checking files · 1 of 100 files"));
            });

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 75,
                FilesTotal: 100,
                CurrentPath: "Reports/file-075.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddMilliseconds(100)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(75).Within(0.01));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Checking files · 75 of 100 files"));
            });
        }


        [Test]
        public async Task RunProgressChanged_UpdatesDirectoryRunProgressState()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingDirectories,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "Reports",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.IsCurrentRunProgressIndeterminate, Is.False);
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Preparing folders · 3 of 10 folders"));
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Preparing folders · 3 of 10 folders"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Preparing folders 3 of 10"));
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
                Assert.That(row.CurrentProgressValue, Is.EqualTo(30).Within(0.01));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Preparing folders 3 of 10"));
            });
        }


        [Test]
        public async Task RunProgressChanged_CoalescesBurstBeforeUiQueue()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new QueuedDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            DateTime occurredAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            for (int index = 0; index < 100; index++)
            {
                string path = "Reports/file-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                    syncPairId,
                    SyncRunProgressStage.ReconcilingFiles,
                    FilesCompleted: index,
                    FilesTotal: 100,
                    CurrentPath: path,
                    StartedAtUtc: occurredAtUtc,
                    IsCompleted: false,
                    OccurredAtUtc: occurredAtUtc.AddMilliseconds(index * 5)));
            }

            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PostedActionCount, Is.EqualTo(1));
                Assert.That(dispatcher.PendingActionCount, Is.EqualTo(1));
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
            });

            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 99 of 100 files"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(99).Within(0.01));
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsLocalScanDiscoveryCount()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ScanningLocal,
                FilesCompleted: 123,
                FilesTotal: null,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Scanning local files · 123 files found · report.txt"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Scanning local files · 123 files found · report.txt"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                Assert.That(row.CurrentOperation, Is.EqualTo("Scanning local files"));
                Assert.That(row.IsCurrentProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsLocalScanCurrentPathBeforeFilesAreFound()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ScanningLocal,
                FilesCompleted: 0,
                FilesTotal: null,
                CurrentPath: "Reports",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Looking for local changes · Reports"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Looking for local changes · Reports"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_ShowsRemoteScanDiscoveryCount()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ScanningRemote,
                FilesCompleted: 123,
                FilesTotal: null,
                CurrentPath: "Reports/report.txt",
                StartedAtUtc: new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 6, 9, 0, 5, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.EqualTo("Documents"));
                Assert.That(viewModel.CurrentRunProgressDetails, Is.EqualTo("Scanning Cotton Cloud · 123 cloud files found · report.txt"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Scanning Cotton Cloud · 123 cloud files found · report.txt"));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.True);
                Assert.That(row.CurrentOperation, Is.EqualTo("Checking cloud"));
                Assert.That(row.IsCurrentProgressIndeterminate, Is.True);
            });
        }


        [Test]
        public async Task RunProgressChanged_KeepsQueuedWorkIndicatorOffForLargeRemoteScan()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ScanningRemote,
                FilesCompleted: 99_300,
                FilesTotal: null,
                CurrentPath: "Photos/2026",
                StartedAtUtc: new DateTime(2026, 6, 16, 19, 31, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 16, 19, 31, 10, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.StartWith("Scanning Cotton Cloud"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
            });
        }


        [Test]
        public async Task RunProgressChanged_KeepsQueuedWorkIndicatorOffForLargePlaceholderCreation()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 1_200,
                FilesTotal: 100_000,
                CurrentPath: "Photos/2026/image-1200.jpg",
                StartedAtUtc: new DateTime(2026, 6, 16, 19, 31, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 16, 19, 31, 10, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressDetails, Does.StartWith("Making cloud files available"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.Empty);
            });
        }
    }
}
