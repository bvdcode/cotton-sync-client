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
        public async Task RunProgressChanged_KeepsGlobalFileRateWhenAggregateTotalGrows()
        {
            Guid firstSyncPairId = Guid.NewGuid();
            Guid secondSyncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(
                CreatePair(firstSyncPairId, "Cloud", "Syncing"),
                CreatePair(secondSyncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                firstSyncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1000,
                CurrentPath: "Cloud/file-0000.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                firstSyncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 100,
                FilesTotal: 1000,
                CurrentPath: "Cloud/file-0100.txt",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(10)));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                secondSyncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 50,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0050.mp4",
                StartedAtUtc: startedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(15)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Syncing 2 folders"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("10 files/s · 2m 05s left"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("150 of 2000 files across 2 folders"));
            });
        }


        [Test]
        public async Task TransferProgressChanged_UsesGlobalFileRateWhenActiveTransferHasNoByteRate()
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
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Videos/clip-0101.mp4",
                TransferredBytes: 1024,
                TotalBytes: 1024 * 1024,
                IsCompleted: false,
                startedAtUtc.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("10 files/s · 1m 30s left"));
                Assert.That(viewModel.CurrentWorkProgressSecondaryDetails, Is.EqualTo("Processing queued changes"));
            });
        }


        [Test]
        public async Task RunProgressChanged_EstimatesFromRecentFileProgressInsteadOfPassStart()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime passStartedAtUtc = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            DateTime reconcileStartedAtUtc = passStartedAtUtc.AddMinutes(5);

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 0,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0000.mp4",
                StartedAtUtc: passStartedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: reconcileStartedAtUtc));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 100,
                FilesTotal: 1000,
                CurrentPath: "Videos/clip-0100.mp4",
                StartedAtUtc: passStartedAtUtc,
                IsCompleted: false,
                OccurredAtUtc: reconcileStartedAtUtc.AddSeconds(10)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.EqualTo("10 files/s · 1m 30s left"));
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Does.Not.Contain("45m"));
            });
        }


        [Test]
        public async Task StatusChanged_ClearsCurrentRunProgressWhenSyncBecomesIdle()
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

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 9, 1, 0, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.CurrentRunProgressTitle, Is.Empty);
                Assert.That(viewModel.CurrentRunProgressDetails, Is.Empty);
                Assert.That(viewModel.CurrentRunProgressValue, Is.Zero);
                Assert.That(row.CurrentOperation, Is.Empty);
                Assert.That(row.HasCurrentOperation, Is.False);
                Assert.That(row.HasCurrentProgress, Is.False);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
                Assert.That(row.CurrentProgressValue, Is.Zero);
            });
        }


        [Test]
        public async Task StatusChanged_ClearsCurrentTransferWhenSyncBecomesIdle()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Reports/report.txt",
                TransferredBytes: 1024,
                TotalBytes: 1024,
                IsCompleted: true,
                new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 9, 1, 0, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.CurrentTransferTitle, Is.Empty);
                Assert.That(viewModel.CurrentTransferDetails, Is.Empty);
                Assert.That(viewModel.CurrentTransferProgressValue, Is.Zero);
                Assert.That(row.CurrentOperation, Is.Empty);
                Assert.That(row.HasCurrentOperation, Is.False);
                Assert.That(row.HasCurrentProgress, Is.False);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
                Assert.That(row.CurrentProgressValue, Is.Zero);
            });
        }


        [Test]
        public async Task Initialize_ShowsFirstSyncPendingUntilPairHasBaseline()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsStatusCardVisible, Is.True);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Waiting for first sync."));
                Assert.That(viewModel.StatusCardTitle, Is.EqualTo("Waiting for first sync."));
                Assert.That(viewModel.HasStatusCardDetail, Is.False);
            });
        }


        [Test]
        public async Task Initialize_ShowsUpToDateAfterPairHasBaseline()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(
                Guid.NewGuid(),
                "Documents",
                "Idle",
                new DateTime(2026, 6, 4, 7, 30, 0, DateTimeKind.Utc))));
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("All folders are up to date."));
            });
        }


        [Test]
        public async Task StatusChanged_UpdatesBaselineAndShowsUpToDateAfterSuccessfulSync()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(
                    syncPairId,
                    "Idle",
                    null,
                    LastSyncedAtUtc: new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc)),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncPairs.Single().LastSyncedAtUtc, Is.EqualTo(new DateTime(2026, 6, 4, 8, 0, 0, DateTimeKind.Utc)));
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("All folders are up to date."));
            });
        }
    }
}
