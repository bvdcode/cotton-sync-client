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
        public async Task SignInCommand_LeavesAddFolderWizardClosedWhenNoSyncPairsExist()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
            };
            controller.RemoteFoldersByPath["/"] = new DesktopRemoteFolderListSnapshot(
                "/",
                [
                    new DesktopRemoteFolderSnapshot(Guid.NewGuid(), "Documents", "/Documents"),
                ]);
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            await ExecuteAsync(viewModel.SignInCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.IsAddSyncPairWizardVisible, Is.False);
                Assert.That(viewModel.IsAddSyncPairLocalStepVisible, Is.False);
                Assert.That(viewModel.HasNoSyncPairs, Is.True);
                Assert.That(viewModel.HeaderTitleText, Is.EqualTo("desktop@example.test"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Connected"));
                Assert.That(viewModel.IsStatusCardVisible, Is.False);
                Assert.That(viewModel.CurrentProgressText, Is.Empty);
                Assert.That(viewModel.RemoteBrowserPath, Is.EqualTo("/"));
                Assert.That(viewModel.RemoteFolderPath, Is.Empty);
                Assert.That(viewModel.RemoteFolders, Is.Empty);
                Assert.That(controller.ListRemoteFolderPaths, Is.Empty);
                Assert.That(controller.SignInRequest?.ServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
            });
        }


        [Test]
        public async Task SignInCommand_ShowsNativeNotificationWhenSupported()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
            };
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            await ExecuteAsync(viewModel.SignInCommand);

            Assert.Multiple(() =>
            {
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Signed in"));
                Assert.That(notificationService.Notifications[0].Message, Is.EqualTo("desktop@example.test"));
            });
        }


        [Test]
        public async Task SignInWithBrowserCommand_UsesVerifiedServerAndAppliesSession()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            await ExecuteAsync(viewModel.SignInWithBrowserCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.HeaderTitleText, Is.EqualTo("browser@example.test"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Connected"));
                Assert.That(viewModel.Password, Is.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
                Assert.That(viewModel.BrowserSignInStatus, Is.Empty);
                Assert.That(viewModel.IsBrowserSignInPending, Is.False);
                Assert.That(controller.BrowserSignInServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
                Assert.That(controller.SignInRequest, Is.Null);
            });
        }


        [Test]
        public async Task SignInWithBrowserCommand_AppliesSessionAfterPendingApproval()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                BrowserSignInCompletion = new TaskCompletionSource<AuthSession>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInWithBrowserCommand.Execute(null);
            await WaitForAsync(() => viewModel.IsBrowserSignInPending);

            controller.BrowserSignInCompletion.SetResult(new AuthSession(
                Guid.NewGuid(),
                "desktop",
                "desktop@example.test",
                false));
            await WaitForAsync(() => viewModel.IsSignedIn);
            await WaitForAsync(() => !viewModel.SignInWithBrowserCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(controller.BrowserSignInServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
                Assert.That(viewModel.IsBrowserSignInPending, Is.False);
                Assert.That(viewModel.IsBusy, Is.False);
                Assert.That(viewModel.IsDashboardVisible, Is.True);
                Assert.That(viewModel.HeaderTitleText, Is.EqualTo("desktop@example.test"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Connected"));
                Assert.That(viewModel.BrowserSignInStatus, Is.Empty);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
            });
        }


        [Test]
        public async Task SignInWithBrowserCommand_CanCancelPendingApproval()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                BrowserSignInCompletion = new TaskCompletionSource<AuthSession>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInWithBrowserCommand.Execute(null);
            await WaitForAsync(() => viewModel.IsBrowserSignInPending);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsBusy, Is.True);
                Assert.That(viewModel.BrowserSignInButtonText, Is.EqualTo("Waiting for approval"));
                Assert.That(viewModel.BrowserSignInStatus, Is.EqualTo("Approve this sign-in in your browser."));
                Assert.That(viewModel.IsPasswordSignInVisible, Is.False);
                Assert.That(viewModel.CancelBrowserSignInCommand.CanExecute(null), Is.True);
                Assert.That(viewModel.SignInWithBrowserCommand.CanExecute(null), Is.False);
                Assert.That(controller.BrowserSignInServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
            });

            await ExecuteAsync(viewModel.CancelBrowserSignInCommand);
            await WaitForAsync(() => viewModel.GlobalStatus == "Sign-in cancelled");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.IsBrowserSignInPending, Is.False);
                Assert.That(viewModel.IsBusy, Is.False);
                Assert.That(viewModel.BrowserSignInStatus, Is.Empty);
                Assert.That(viewModel.IsPasswordSignInVisible, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sign-in cancelled"));
                Assert.That(viewModel.Activities.First().Details, Is.EqualTo("Browser sign-in cancelled"));
            });
        }


        [Test]
        public async Task DisposeAsync_CancelsPendingBrowserSignIn()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                BrowserSignInCompletion = new TaskCompletionSource<AuthSession>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInWithBrowserCommand.Execute(null);
            await WaitForAsync(() => viewModel.IsBrowserSignInPending);

            await viewModel.DisposeAsync();
            await WaitForAsync(() => controller.BrowserSignInCompletion.Task.IsCanceled);
            await WaitForAsync(() => !viewModel.SignInWithBrowserCommand.IsRunning);

            Assert.Multiple(() =>
            {
                Assert.That(controller.BrowserSignInCompletion.Task.IsCanceled, Is.True);
                Assert.That(viewModel.IsBrowserSignInPending, Is.False);
                Assert.That(viewModel.IsBusy, Is.False);
            });
        }


        [Test]
        public async Task SignInCommand_DoesNotShowNativeNotificationWhenDisabled()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot(enableNotifications: false))
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
            };
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            await ExecuteAsync(viewModel.SignInCommand);

            Assert.That(notificationService.Notifications, Is.Empty);
        }


        [Test]
        public async Task InitializeAsync_ShowsSessionRestoredNotificationWhenVisibleLaunchAllowsIt()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(
                controller,
                notificationService: notificationService,
                notifyOnSessionRestore: true);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Session restored"));
                Assert.That(notificationService.Notifications[0].Message, Is.EqualTo("vadim@example.com"));
            });
        }


        [Test]
        public async Task InitializeAsync_DoesNotShowSessionRestoredNotificationWhenStartupNoiseSuppressed()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot());
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(
                controller,
                notificationService: notificationService,
                notifyOnSessionRestore: false);

            await viewModel.InitializeAsync();

            Assert.That(notificationService.Notifications, Is.Empty);
        }


        [Test]
        public async Task InitializeAsync_DoesNotShowSessionRestoredNotificationWhenNotificationsDisabled()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(
                CreateSignedInSnapshotWithNotifications(enableNotifications: false));
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(
                controller,
                notificationService: notificationService,
                notifyOnSessionRestore: true);

            await viewModel.InitializeAsync();

            Assert.That(notificationService.Notifications, Is.Empty);
        }
    }
}
