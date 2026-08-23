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
        public async Task PauseResumeCommands_AreMutuallyAvailable()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanPauseSync, Is.True);
                Assert.That(viewModel.CanResumeSync, Is.False);
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.True);
                Assert.That(viewModel.CanShowPauseResumeTrayAction, Is.True);
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Pause sync"));
                Assert.That(viewModel.PauseResumeTrayLabel, Is.EqualTo("Pause"));
                Assert.That(viewModel.SyncNowCommand.CanExecute(null), Is.True);
            });

            await ExecuteAsync(viewModel.PauseResumeCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.PauseAllCalls, Is.EqualTo(1));
                Assert.That(viewModel.CanPauseSync, Is.False);
                Assert.That(viewModel.CanResumeSync, Is.True);
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.True);
                Assert.That(viewModel.CanShowPauseResumeTrayAction, Is.True);
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Resume sync"));
                Assert.That(viewModel.PauseResumeTrayLabel, Is.EqualTo("Resume"));
                Assert.That(viewModel.SyncNowCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sync is paused."));
            });

            await ExecuteAsync(viewModel.PauseResumeCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.ResumeAllCalls, Is.EqualTo(1));
                Assert.That(viewModel.CanPauseSync, Is.True);
                Assert.That(viewModel.CanResumeSync, Is.False);
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.True);
                Assert.That(viewModel.CanShowPauseResumeTrayAction, Is.True);
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Pause sync"));
                Assert.That(viewModel.PauseResumeTrayLabel, Is.EqualTo("Pause"));
                Assert.That(viewModel.SyncNowCommand.CanExecute(null), Is.True);
            });
        }


        [Test]
        public async Task GlobalControls_RemainAvailableWhileManualSyncIsRunning()
        {
            TaskCompletionSource<bool> syncAllCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                SyncAllCompletion = syncAllCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.SyncNowCommand.Execute(null);
            await WaitForAsync(() => viewModel.IsBusy && controller.SyncAllCalls == 1);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SyncNowCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.ShowSettingsCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.CanPauseSync, Is.True);
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.True);
                Assert.That(viewModel.PauseResumeCommand.CanExecute(null), Is.True);
            });

            await ExecuteAsync(viewModel.PauseResumeCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.PauseAllCalls, Is.EqualTo(1));
                Assert.That(viewModel.IsBusy, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
                Assert.That(viewModel.PauseResumeTrayLabel, Is.EqualTo("Resume"));
            });

            syncAllCompletion.SetResult(true);
            await WaitForAsync(() => !viewModel.SyncNowCommand.IsRunning);

            Assert.That(viewModel.IsBusy, Is.False);
        }


        [Test]
        public async Task PauseResumeCommand_ShowsPausingWhilePauseRequestIsRunning()
        {
            TaskCompletionSource<bool> pauseAllCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")))
            {
                PauseAllCompletion = pauseAllCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.PauseResumeCommand.Execute(null);
            await WaitForAsync(() => viewModel.PauseResumeCommand.IsRunning && controller.PauseAllCalls == 1);

            SyncPairRowViewModel row = viewModel.SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSyncPausePending, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Pausing"));
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Pausing sync"));
                Assert.That(viewModel.PauseResumeTrayLabel, Is.EqualTo("Pausing"));
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.False);
                Assert.That(viewModel.CanShowPauseResumeTrayAction, Is.True);
                Assert.That(viewModel.PauseResumeCommand.CanExecute(null), Is.False);
                Assert.That(row.Status, Is.EqualTo("Pausing"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Videos: Pausing"));
            });

            pauseAllCompletion.SetResult(true);
            await WaitForAsync(() => !viewModel.PauseResumeCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSyncPausePending, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.True);
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Resume sync"));
            });
        }


        [Test]
        public async Task PauseResumeCommand_RemainsAvailableDuringBackgroundSyncProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Videos", "Syncing")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportRunProgress(new DesktopRunProgressSnapshot(
                syncPairId,
                SyncRunProgressStage.ReconcilingFiles,
                FilesCompleted: 5,
                FilesTotal: 20,
                CurrentPath: "Videos/clip.mp4",
                StartedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc),
                IsCompleted: false,
                OccurredAtUtc: new DateTime(2026, 6, 4, 9, 0, 5, DateTimeKind.Utc)));
            controller.ReportTransferProgress(new DesktopTransferProgressSnapshot(
                syncPairId,
                SyncTransferDirection.Download,
                "Videos/clip.mp4",
                TransferredBytes: 512,
                TotalBytes: 1024,
                IsCompleted: false,
                new DateTime(2026, 6, 4, 9, 0, 7, DateTimeKind.Utc)));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Syncing"));
                Assert.That(viewModel.CanPauseSync, Is.True);
                Assert.That(viewModel.CanTogglePauseResumeSync, Is.True);
                Assert.That(viewModel.PauseResumeCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Pause sync"));
                Assert.That(viewModel.PauseResumeTrayLabel, Is.EqualTo("Pause"));
            });
        }


        [Test]
        public async Task PauseResumeCommand_OffersPauseWhileResumedSyncIsStillRunning()
        {
            TaskCompletionSource<bool> resumeAllCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Cloud", "Paused")))
            {
                ResumeAllCompletion = resumeAllCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            AsyncRelayCommand resumeCommand = viewModel.PauseResumeCommand;
            resumeCommand.Execute(null);
            await WaitForAsync(() => resumeCommand.IsRunning && controller.ResumeAllCalls == 1);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.PauseResumeCommand, Is.SameAs(viewModel.PauseCommand));
                Assert.That(viewModel.PauseResumeCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.PauseResumeSyncLabel, Is.EqualTo("Pause sync"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Resuming"));
            });

            await ExecuteAsync(viewModel.PauseResumeCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.PauseAllCalls, Is.EqualTo(1));
                Assert.That(viewModel.IsSyncPaused, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
            });

            resumeAllCompletion.SetResult(true);
            await WaitForAsync(() => !resumeCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSyncPaused, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
            });
        }


        [Test]
        public async Task GlobalSyncCommands_DoNotChangeDisabledPairRows()
        {
            Guid enabledPairId = Guid.NewGuid();
            Guid disabledPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(enabledPairId, "Documents", "Idle"),
                    CreatePair(disabledPairId, "Archive", "Disabled")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.SyncNowCommand);

            SyncPairRowViewModel enabledPair = viewModel.SyncPairs.Single(pair => pair.Id == enabledPairId);
            SyncPairRowViewModel disabledPair = viewModel.SyncPairs.Single(pair => pair.Id == disabledPairId);
            Assert.Multiple(() =>
            {
                Assert.That(enabledPair.Status, Is.EqualTo("Idle"));
                Assert.That(enabledPair.CurrentOperation, Is.Empty);
                Assert.That(disabledPair.Status, Is.EqualTo("Disabled"));
                Assert.That(disabledPair.CurrentOperation, Is.Empty);
            });

            await ExecuteAsync(viewModel.PauseCommand);

            Assert.Multiple(() =>
            {
                Assert.That(enabledPair.Status, Is.EqualTo("Paused"));
                Assert.That(disabledPair.Status, Is.EqualTo("Disabled"));
            });

            await ExecuteAsync(viewModel.ResumeCommand);

            Assert.Multiple(() =>
            {
                Assert.That(enabledPair.Status, Is.EqualTo("Idle"));
                Assert.That(disabledPair.Status, Is.EqualTo("Disabled"));
            });
        }


        [Test]
        public async Task OpenFolderCommand_UsesRowParameterWhenProvided()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Idle"),
                    CreatePair(Guid.NewGuid(), "Pictures", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.OpenFolderCommand, viewModel.SyncPairs[1]);

            Assert.That(controller.OpenedFolderPath, Is.EqualTo("/home/vadim/Pictures"));
        }


        [Test]
        public async Task OpenTrayFolderCommand_OpensSingleSyncPair()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanOpenTrayFolder, Is.True);
                Assert.That(viewModel.TrayOpenFolderLabel, Is.EqualTo("Open local folder"));
                Assert.That(viewModel.OpenTrayFolderCommand.CanExecute(null), Is.True);
            });

            await ExecuteAsync(viewModel.OpenTrayFolderCommand);

            Assert.That(controller.OpenedFolderPath, Is.EqualTo("/home/vadim/Documents"));
        }


        [Test]
        public async Task OpenTrayFolderCommand_IsDisabledForMultipleSyncPairs()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(Guid.NewGuid(), "Documents", "Idle"),
                    CreatePair(Guid.NewGuid(), "Pictures", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanOpenTrayFolder, Is.False);
                Assert.That(viewModel.TrayOpenFolderLabel, Is.EqualTo("Open local folder"));
                Assert.That(viewModel.OpenTrayFolderCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task StatusChanged_UpdatesCurrentProgressText()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(syncPairId, "Syncing", null, "Uploading report.txt"),
            ]));

            SyncPairRowViewModel row = viewModel.SyncPairs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(row.CurrentOperation, Is.EqualTo("Uploading report.txt"));
                Assert.That(row.HasCurrentOperation, Is.True);
                Assert.That(row.HasCurrentProgress, Is.True);
                Assert.That(row.IsCurrentProgressIndeterminate, Is.True);
                Assert.That(row.CurrentProgressValue, Is.Zero);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Syncing"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Documents: Uploading report.txt"));
            });
        }


        [Test]
        public async Task StatusChanged_TreatsDisabledPairsAsOutOfScopeForPausedGlobalStatus()
        {
            Guid enabledPairId = Guid.NewGuid();
            Guid disabledPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshot(
                    CreatePair(enabledPairId, "Documents", "Idle"),
                    CreatePair(disabledPairId, "Archive", "Disabled")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            controller.ReportStatus(new DesktopSyncStatusSnapshot(
            [
                new DesktopSyncPairStatusSnapshot(enabledPairId, "Paused", null),
                new DesktopSyncPairStatusSnapshot(disabledPairId, "Disabled", null),
            ]));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Paused"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Paused"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sync is paused."));
                Assert.That(viewModel.CanResumeSync, Is.True);
                Assert.That(viewModel.CanPauseSync, Is.False);
            });
        }
    }
}
