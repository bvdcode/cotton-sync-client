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
        public async Task TransferProgressChanged_UpdatesCurrentTransferState()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.IsCurrentTransferIndeterminate, Is.False);
                Assert.That(viewModel.CurrentTransferProgressValue, Is.EqualTo(50).Within(0.01));
                Assert.That(viewModel.CurrentTransferTitle, Is.EqualTo("Documents: Uploading report.txt"));
                Assert.That(viewModel.CurrentTransferDetails, Is.EqualTo("512 B / 1.0 KB"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Uploading report.txt"));
                Assert.That(row.HasCurrentOperation, Is.True);
                Assert.That(row.HasCurrentProgress, Is.True);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.False);
                Assert.That(row.CurrentProgressValue, Is.EqualTo(50).Within(0.01));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Uploading report.txt"));
            });
        }


        [Test]
        public async Task TransferProgressChanged_ShowsHashProgressAsChecking()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Hash,
                "2026/video.mp4",
                TransferredBytes: 256,
                TotalBytes: 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.CurrentTransferTitle, Is.EqualTo("Videos: Checking video.mp4"));
                Assert.That(viewModel.CurrentTransferDetails, Is.EqualTo("256 B / 1.0 KB"));
                Assert.That(row.CurrentOperation, Is.EqualTo("Checking video.mp4"));
                Assert.That(row.CurrentProgressValue, Is.EqualTo(25).Within(0.01));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Videos: Checking video.mp4"));
            });
        }


        [Test]
        public async Task TransferProgressChanged_DoesNotCountHashBytesAsRunTransferBytes()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 7, 9, 0, 0, DateTimeKind.Utc);
            const long totalRunBytes = 10L * 1024 * 1024 * 1024;
            const long completedRunBytes = 3L * 1024 * 1024 * 1024;

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
                SyncTransferDirection.Hash,
                "Videos/clip.mp4",
                TransferredBytes: 512L * 1024 * 1024,
                TotalBytes: 1024L * 1024 * 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAtUtc.AddSeconds(61),
                SpeedBytesPerSecond: 256L * 1024 * 1024,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentTransferTitle, Is.EqualTo("Videos: Checking clip.mp4"));
                Assert.That(viewModel.CurrentWorkProgressHeaderSizeDetails, Is.EqualTo("3.0 GB / 10 GB"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("Checking files · 200 of 29189 files"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(30).Within(0.01));
            });
        }


        [Test]
        public async Task TransferProgressChanged_ShowsSyncingHeaderEvenWhenLatestStatusIsIdle()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Archive/09.7z",
                TransferredBytes: 25L * 1024L * 1024L * 1024L,
                TotalBytes: 28L * 1024L * 1024L * 1024L,
                IsCompleted: false,
                new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentTransferTitle, Is.EqualTo("Videos: Downloading 09.7z"));
            });
        }


        [Test]
        public async Task TransferProgressChanged_ShowsTransferSpeedAndRemainingTime()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Reports/report.txt",
                TransferredBytes: 2 * 1024 * 1024,
                TotalBytes: 10 * 1024 * 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                SpeedBytesPerSecond: 1024 * 1024,
                EstimatedTimeRemaining: TimeSpan.FromSeconds(8)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentWorkProgress, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents: Downloading report.txt"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("2.0 MB / 10 MB · 1.0 MB/s · 8s left"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(20).Within(0.01));
                Assert.That(viewModel.IsCurrentWorkProgressIndeterminate, Is.False);
            });
        }


        [Test]
        public async Task TransferProgressChanged_CoalescesBurstBeforeUiQueue()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            QueuedDesktopUiDispatcher dispatcher = new QueuedDesktopUiDispatcher();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            DateTime occurredAtUtc = new(2026, 6, 6, 10, 0, 0, DateTimeKind.Utc);

            for (int index = 0; index < 100; index++)
            {
                controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                    syncPairId,
                    SyncTransferDirection.Upload,
                    "Reports/report.txt",
                    TransferredBytes: index * 1024,
                    TotalBytes: 100 * 1024,
                    IsCompleted: false,
                    occurredAtUtc.AddMilliseconds(index * 5)));
            }

            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PostedActionCount, Is.EqualTo(1));
                Assert.That(dispatcher.PendingActionCount, Is.EqualTo(1));
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
            });

            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentTransfer, Is.True);
                Assert.That(viewModel.CurrentWorkProgressTitle, Is.EqualTo("Documents: Uploading report.txt"));
                Assert.That(viewModel.CurrentWorkProgressDetails, Is.EqualTo("99 KB / 100 KB"));
                Assert.That(viewModel.CurrentWorkProgressValue, Is.EqualTo(99).Within(0.01));
            });
        }


        [Test]
        public async Task TransferProgressChanged_ThrottlesVisibleUpdates()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 20,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 40,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc.AddMilliseconds(50)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentTransferProgressValue, Is.EqualTo(20).Within(0.01));
                Assert.That(viewModel.CurrentTransferDetails, Is.EqualTo("20 B / 100 B"));
            });

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 60,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc.AddMilliseconds(100)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CurrentTransferProgressValue, Is.EqualTo(60).Within(0.01));
                Assert.That(viewModel.CurrentTransferDetails, Is.EqualTo("60 B / 100 B"));
            });
        }


        [Test]
        public async Task TransferProgressChanged_ClearsCompletedTransferState()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAtUtc = new(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);

            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 20,
                TotalBytes: 100,
                IsCompleted: false,
                startedAtUtc));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Upload,
                "Reports/report.txt",
                TransferredBytes: 100,
                TotalBytes: 100,
                IsCompleted: true,
                startedAtUtc.AddMilliseconds(100)));

            Assert.Multiple(() =>
            {
                SyncPairRowViewModel row = viewModel.SyncPairs.Single();
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.CurrentTransferProgressValue, Is.Zero);
                Assert.That(viewModel.CurrentTransferTitle, Is.Empty);
                Assert.That(viewModel.CurrentTransferDetails, Is.Empty);
                Assert.That(row.CurrentOperation, Is.Empty);
                Assert.That(row.HasCurrentProgress, Is.False);
            });
        }
    }
}
