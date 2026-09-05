// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [Test]
        public async Task PauseCommand_ClearsActiveProgressWhenControllerCompletesWithoutStatusEvent()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            DateTime startedAt = DateTime.UtcNow;
            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 3,
                FilesTotal: 10,
                CurrentPath: "report.txt",
                StartedAtUtc: startedAt,
                IsCompleted: false,
                OccurredAtUtc: startedAt));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "report.txt",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                OccurredAtUtc: startedAt));
            Assert.That(viewModel.HasCurrentWorkProgress, Is.True);

            await ExecuteTrackedCommandAsync(viewModel.PauseCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Paused"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sync is paused."));
                Assert.That(viewModel.HasCurrentWorkProgress, Is.False);
                Assert.That(viewModel.HasCurrentTransfer, Is.False);
                Assert.That(viewModel.HasCurrentRunProgress, Is.False);
                Assert.That(viewModel.CurrentWorkProgressHeaderRateDetails, Is.Empty);
                Assert.That(viewModel.SyncPairs.Single().HasCurrentProgress, Is.False);
            });
        }

        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        public async Task PauseResumeCommand_CompletionDoesNotChangeAnotherSession(
            bool pause,
            bool restoreSession,
            bool fail)
        {
            TaskCompletionSource<bool> commandCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", pause ? "Idle" : "Paused")));
            if (pause)
            {
                controller.PauseAllCompletion = commandCompletion;
            }
            else
            {
                controller.ResumeAllCompletion = commandCompletion;
            }

            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            Task command = ExecuteTrackedCommandAsync(viewModel.PauseResumeCommand);
            try
            {
                await ExecuteTrackedCommandAsync(viewModel.SignOutCommand);
                bool pausePendingAfterSignOut = viewModel.IsSyncPausePending;
                if (restoreSession)
                {
                    await viewModel.InitializeAsync();
                }

                string expectedGlobalStatus = viewModel.GlobalStatus;
                string expectedPairStatus = viewModel.SyncPairs.Single().Status;
                int expectedActivityCount = viewModel.Activities.Count;
                if (fail)
                {
                    commandCompletion.SetException(new IOException("The previous session has ended."));
                }
                else
                {
                    commandCompletion.SetResult(true);
                }

                await command;

                Assert.Multiple(() =>
                {
                    Assert.That(pausePendingAfterSignOut, Is.False);
                    Assert.That(viewModel.IsSyncPausePending, Is.False);
                    Assert.That(viewModel.IsSignedIn, Is.EqualTo(restoreSession));
                    Assert.That(viewModel.GlobalStatus, Is.EqualTo(expectedGlobalStatus));
                    Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo(expectedPairStatus));
                    Assert.That(viewModel.Activities.Count, Is.EqualTo(expectedActivityCount));
                    Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                    Assert.That(viewModel.HasCurrentWorkProgress, Is.False);
                });
            }
            finally
            {
                commandCompletion.TrySetResult(true);
                await command;
            }
        }
    }
}
