// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [Test]
        public async Task SyncNowCommand_CompletionPreservesSubsequentPause()
        {
            TaskCompletionSource<bool> syncCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Syncing")))
            {
                SyncAllCompletion = syncCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            Task syncCommand = ExecuteTrackedCommandAsync(viewModel.SyncNowCommand);
            Assert.That(controller.SyncAllCalls, Is.EqualTo(1));
            await ExecuteTrackedCommandAsync(viewModel.PauseCommand);
            syncCompletion.SetResult(true);
            await syncCommand;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSyncPaused, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Paused"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sync is paused."));
                Assert.That(viewModel.IsBusy, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SignOutCommand_DiscardsPreviouslyQueuedSessionEvents(bool restoreSession)
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            QueuedDesktopUiDispatcher dispatcher = new();
            using ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            dispatcher.DrainAll();
            QueueActiveSessionEvents(controller, syncPairId);

            await ExecuteTrackedCommandAsync(viewModel.SignOutCommand);
            if (restoreSession)
            {
                await viewModel.InitializeAsync();
            }

            int activityCount = viewModel.Activities.Count;
            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.EqualTo(restoreSession));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo(restoreSession ? "Connected" : "Signed out"));
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.HasNotifications, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
                Assert.That(viewModel.Activities.Count, Is.EqualTo(activityCount));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Dispose_DiscardsPreviouslyQueuedSessionEvents(bool asynchronously)
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            QueuedDesktopUiDispatcher dispatcher = new();
            ShellViewModel viewModel = CreateViewModel(controller, uiDispatcher: dispatcher);
            await viewModel.InitializeAsync();
            dispatcher.DrainAll();
            string globalStatus = viewModel.GlobalStatus;
            int activityCount = viewModel.Activities.Count;
            QueueActiveSessionEvents(controller, syncPairId);

            if (asynchronously)
            {
                await viewModel.DisposeAsync();
            }
            else
            {
                viewModel.Dispose();
            }

            dispatcher.DrainAll();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo(globalStatus));
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
                Assert.That(viewModel.Activities.Count, Is.EqualTo(activityCount));
            });
        }

        private static void QueueActiveSessionEvents(FakeDesktopShellController controller, Guid syncPairId)
        {
            DateTime startedAt = new(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc);
            controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null)]));
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "report.txt",
                StartedAtUtc: startedAt,
                IsCompleted: false,
                OccurredAtUtc: startedAt.AddSeconds(1)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "report.txt",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAt.AddSeconds(2)));
            controller.ReportActivity(new DesktopActivitySnapshot(
                "Conflict", "report.txt", "Conflicting edits require review.", startedAt.AddSeconds(3), syncPairId));
        }

        private static async Task ExecuteTrackedCommandAsync(AsyncRelayCommand command)
        {
            Assert.That(command.CanExecute(null), Is.True);
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCanExecuteChanged(object? sender, EventArgs eventArgs)
            {
                if (!command.IsRunning)
                {
                    completed.TrySetResult();
                }
            }

            command.CanExecuteChanged += OnCanExecuteChanged;
            try
            {
                command.Execute(null);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                command.CanExecuteChanged -= OnCanExecuteChanged;
            }
        }
    }
}
