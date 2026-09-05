// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public async Task SyncCommand_CompletionCannotChangeFollowingSession(
            bool approveRemoteDelete,
            bool failSync,
            bool completeNewSession)
        {
            Guid syncPairId = Guid.NewGuid();
            TaskCompletionSource<bool> syncCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<AuthSession> signInCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")))
            {
                SyncAllCompletion = syncCompletion,
                BrowserSignInCompletion = signInCompletion,
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://cloud.example.test/"), true, "Cotton Cloud", "instance-hash"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            if (approveRemoteDelete)
            {
                controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [
                    new DesktopSyncPairStatusSnapshot(
                        syncPairId,
                        "Error",
                        "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100. "
                        + "Plan fingerprint " + RemoteDeletePlanFingerprint + "."),
                ]));
            }

            AsyncRelayCommand command = approveRemoteDelete
                ? viewModel.ApproveRemoteMassDeleteCommand
                : viewModel.SyncNowCommand;
            Task syncCommand = ExecuteTrackedCommandAsync(command);
            Assert.That(controller.SyncAllCalls, Is.EqualTo(1));
            Assert.That(viewModel.IsBusy, Is.True);
            await ExecuteTrackedCommandAsync(viewModel.SignOutCommand);
            Assert.That(command.IsRunning, Is.True);
            Assert.That(viewModel.IsBusy, Is.False);
            await EnableBrowserSignInAsync(viewModel);

            AuthSession nextSession = new(Guid.NewGuid(), "next", "next@example.test", false);
            Task signInCommand = ExecuteTrackedCommandAsync(viewModel.SignInWithBrowserCommand);
            try
            {
                if (completeNewSession)
                {
                    signInCompletion.SetResult(nextSession);
                    await signInCommand;
                }

                string status = viewModel.GlobalStatus;
                int activityCount = viewModel.Activities.Count;
                if (failSync)
                {
                    syncCompletion.SetException(new InvalidOperationException("Previous session sync failed."));
                }
                else
                {
                    syncCompletion.SetResult(true);
                }

                await syncCommand;

                Assert.Multiple(() =>
                {
                    Assert.That(viewModel.IsSignedIn, Is.EqualTo(completeNewSession));
                    Assert.That(viewModel.GlobalStatus, Is.EqualTo(status));
                    Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                    Assert.That(viewModel.Activities.Count, Is.EqualTo(activityCount));
                    Assert.That(viewModel.IsBusy, Is.EqualTo(!completeNewSession));
                });
            }
            finally
            {
                signInCompletion.TrySetResult(nextSession);
                await signInCommand;
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task SyncCommand_SessionRevocationRestoresSignIn(bool approveRemoteDelete, bool failSync)
        {
            Guid syncPairId = Guid.NewGuid();
            TaskCompletionSource<bool> syncCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new(
                CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Idle")))
            {
                SyncAllCompletion = syncCompletion,
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://cloud.example.test/"), true, "Cotton Cloud", "instance-hash"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            await EnableBrowserSignInAsync(viewModel);
            if (approveRemoteDelete)
            {
                controller.ReportStatus(new DesktopSyncStatusSnapshot(
                [
                    new DesktopSyncPairStatusSnapshot(
                        syncPairId,
                        "Error",
                        "Remote delete blocked by mass-delete guard. 2207 pending deletes exceed limit 100. "
                        + "Plan fingerprint " + RemoteDeletePlanFingerprint + "."),
                ]));
            }

            AsyncRelayCommand command = approveRemoteDelete
                ? viewModel.ApproveRemoteMassDeleteCommand
                : viewModel.SyncNowCommand;
            Task syncCommand = ExecuteTrackedCommandAsync(command);
            Assert.That(controller.SyncAllCalls, Is.EqualTo(1));
            Assert.That(viewModel.IsBusy, Is.True);
            controller.ReportSessionRevoked(new DesktopSessionRevocationSnapshot(DateTime.UtcNow));
            bool canSignInAfterRevocation = viewModel.SignInWithBrowserCommand.CanExecute(null);
            if (failSync)
            {
                syncCompletion.SetException(new InvalidOperationException("Revoked session sync failed."));
            }
            else
            {
                syncCompletion.SetResult(true);
            }

            await syncCommand;

            Assert.Multiple(() =>
            {
                Assert.That(canSignInAfterRevocation, Is.True);
                Assert.That(viewModel.IsBusy, Is.False);
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Session expired"));
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.SignInWithBrowserCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.ChangeServerCommand.CanExecute(null), Is.True);
            });
        }

        private static async Task EnableBrowserSignInAsync(ShellViewModel viewModel)
        {
            TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCanExecuteChanged(object? sender, EventArgs eventArgs)
            {
                if (viewModel.SignInWithBrowserCommand.CanExecute(null))
                {
                    ready.TrySetResult();
                }
            }

            viewModel.SignInWithBrowserCommand.CanExecuteChanged += OnCanExecuteChanged;
            try
            {
                viewModel.ServerUrl = "https://cloud.example.test/";
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                viewModel.SignInWithBrowserCommand.CanExecuteChanged -= OnCanExecuteChanged;
            }
        }
    }
}
