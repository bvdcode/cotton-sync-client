// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase("Paused")]
        [TestCase("Disabled")]
        public async Task StatusChanged_TerminalControlClearsFreshVirtualFilesProgress(string status)
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAt = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            controller.ReportRunProgress(CreatePendingCloudFilesProgress(syncPairId, startedAt));

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, status, null)]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo(status));
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
                Assert.That(viewModel.CanResumeSync, Is.EqualTo(status == "Paused"));
            });

            controller.ReportRunProgress(CreatePendingCloudFilesProgress(syncPairId, startedAt) with
            {
                FilesCompleted = 4,
                OccurredAtUtc = startedAt.AddSeconds(2),
            });
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId, SyncTransferDirection.Download, "report.txt", 512, 1024, false, startedAt.AddSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo(status));
            });
        }

        [Test]
        public async Task StatusChanged_CoalescingDoesNotCompleteVirtualFilesBeforeQueuedProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            dispatcher.DrainAll();
            DateTime startedAt = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            DesktopSyncStatusSnapshot syncingStatus = new(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null)]);
            controller.ReportStatus(syncingStatus);
            dispatcher.DrainAll();

            controller.ReportStatus(syncingStatus);
            controller.ReportRunProgress(CreatePendingCloudFilesProgress(syncPairId, startedAt));
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null, LastSyncedAtUtc: startedAt.AddSeconds(2))]));
            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNotifications, Is.False);
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Syncing"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Syncing"));
            });

            controller.ReportRunProgress(CreatePendingCloudFilesProgress(syncPairId, startedAt) with
            {
                FilesCompleted = 100,
                IsCompleted = true,
                OccurredAtUtc = startedAt.AddSeconds(3),
            });
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null, LastSyncedAtUtc: startedAt.AddSeconds(4))]));
            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
                Assert.That(viewModel.Notifications.Single().Title, Is.EqualTo("Initial sync complete"));
            });
        }

        [Test]
        public async Task RunProgressChanged_IgnoresOlderSampleAfterRunCompletion()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAt = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            DesktopRunProgressSnapshot progress = new(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "report.txt",
                StartedAtUtc: startedAt,
                IsCompleted: false,
                OccurredAtUtc: startedAt.AddSeconds(1));
            controller.ReportRunProgress(progress);
            controller.ReportRunProgress(progress with
            {
                Stage = SyncRunProgressStage.Completed,
                FilesCompleted = 10,
                IsCompleted = true,
                OccurredAtUtc = startedAt.AddSeconds(3),
            });
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Idle", null, LastSyncedAtUtc: startedAt.AddSeconds(3))]));

            controller.ReportRunProgress(progress with
            {
                FilesCompleted = 4,
                OccurredAtUtc = startedAt.AddSeconds(2),
            });

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Connected"));
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunProgressChanged_PreservesNewerPassWhenOlderCompletionArrivesLate(bool queued)
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: queued ? dispatcher : null);
            await viewModel.InitializeAsync();
            DateTime startedAt = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            DesktopRunProgressSnapshot oldProgress = new(
                syncPairId, SyncRunProgressStage.Completed, 10, 10,
                string.Empty, startedAt, true, startedAt.AddSeconds(3));
            controller.ReportRunProgress(oldProgress);
            dispatcher.DrainAll();
            DesktopRunProgressSnapshot newProgress = oldProgress with
            {
                Stage = SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted = 2,
                CurrentPath = "next.txt",
                StartedAtUtc = startedAt.AddSeconds(4),
                OccurredAtUtc = startedAt.AddSeconds(5),
                IsCompleted = false,
            };

            controller.ReportRunProgress(newProgress);
            controller.ReportRunProgress(oldProgress);
            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(20));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Syncing"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunProgressChanged_AcceptsLaterAvailabilityPhaseWithEarlierStart(bool queued)
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(CreateSignedInSnapshot(
                CreatePair(syncPairId, "Cloud", "Syncing", mode: SyncPairMode.WindowsVirtualFiles)));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: queued ? dispatcher : null);
            await viewModel.InitializeAsync();
            DateTime requestStartedAt = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId, SyncRunProgressStage.HydratingCloudFiles, 1, 1, "keep.txt",
                requestStartedAt.AddSeconds(1), true, requestStartedAt.AddSeconds(2)));

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId, SyncRunProgressStage.DehydratingCloudFiles, 1, 2, "free.txt",
                requestStartedAt, false, requestStartedAt.AddSeconds(3)));
            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasCurrentRunProgress, Is.True);
                Assert.That(viewModel.CurrentRunProgressValue, Is.EqualTo(50));
                Assert.That(viewModel.CurrentTrayActivityKind, Is.EqualTo(DesktopTrayActivityKind.FreeingSpace));
            });
        }

        private static DesktopRunProgressSnapshot CreatePendingCloudFilesProgress(Guid syncPairId, DateTime startedAt)
        {
            return new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.CreatingPlaceholders,
                FilesCompleted: 3,
                FilesTotal: 100,
                CurrentPath: "report.txt",
                StartedAtUtc: startedAt,
                IsCompleted: false,
                OccurredAtUtc: startedAt.AddSeconds(1));
        }
    }
}
