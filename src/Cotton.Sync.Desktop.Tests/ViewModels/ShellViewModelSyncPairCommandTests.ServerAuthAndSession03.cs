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
        public async Task SignInCommand_ShowsSetupErrorWhenAuthenticationFails()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                SignInException = new InvalidOperationException("Invalid username, password, or two-factor code."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "wrong-password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInCommand.Execute(null);
            await WaitForAsync(() => viewModel.HasActionRequired);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSetupVisible, Is.True);
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Invalid username, password, or two-factor code."));
            });
        }


        [Test]
        public async Task SignInCommand_ShowsHumanTotpRequiredMessage()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                SignInException = new CottonApiException(
                    HttpStatusCode.Forbidden,
                    "{\"success\":false,\"message\":\"Two-factor authentication code is required\"}",
                    "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden)."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInCommand.Execute(null);
            await WaitForAsync(() => viewModel.HasActionRequired);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Enter the 2FA code for this account."));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sign-in failed"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sign in to continue."));
            });
        }


        [Test]
        public async Task SignInCommand_RetriesSuccessfullyAfterTotpRequired()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                SignInException = new CottonApiException(
                    HttpStatusCode.Forbidden,
                    "{\"success\":false,\"message\":\"Two-factor authentication code is required\"}",
                    "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden)."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInCommand.Execute(null);
            await WaitForAsync(() => viewModel.HasActionRequired);

            controller.SignInException = null;
            viewModel.TotpCode = "123456";
            await ExecuteAsync(viewModel.SignInCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.SignInRequest?.TotpCode, Is.EqualTo("123456"));
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.IsDashboardVisible, Is.True);
                Assert.That(viewModel.IsSetupVisible, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Connected"));
                Assert.That(viewModel.Password, Is.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
            });
        }


        [Test]
        public async Task SignInCommand_ShowsHumanInvalidPasswordMessage()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
                SignInException = new CottonApiException(
                    HttpStatusCode.Forbidden,
                    "{\"success\":false,\"message\":\"Invalid password\"}",
                    "Cotton API request POST /api/v1/auth/login failed with status 403 (Forbidden)."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "wrong-password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            viewModel.SignInCommand.Execute(null);
            await WaitForAsync(() => viewModel.HasActionRequired);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Invalid username or password."));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Sign-in failed"));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sign in to continue."));
            });
        }


        [Test]
        public async Task SignOutCommand_ClearsSensitiveSetupState()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();
            viewModel.Password = "password";
            viewModel.TotpCode = "123456";
            await ExecuteAsync(viewModel.ShowSettingsCommand);

            await ExecuteAsync(viewModel.SignOutCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.SignOutCalls, Is.EqualTo(1));
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.IsSetupVisible, Is.True);
                Assert.That(viewModel.AccountName, Is.EqualTo("Signed out"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Signed out"));
                Assert.That(viewModel.Password, Is.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
                Assert.That(viewModel.IsSettingsVisible, Is.False);
                Assert.That(viewModel.IsDashboardChromeVisible, Is.True);
                Assert.That(viewModel.SignOutCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task SignOutThenSignInAgain_ReusesSameInstallationFlow()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")))
            {
                ServerProbeResult = new DesktopServerProbeResult(
                    new Uri("https://app.cottoncloud.dev/"),
                    true,
                    "Cotton Cloud",
                    "instance-hash"),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.SignOutCommand);
            viewModel.ServerUrl = "app.cottoncloud.dev";
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);
            await ExecuteAsync(viewModel.SignInCommand);

            Assert.Multiple(() =>
            {
                Assert.That(controller.SignOutCalls, Is.EqualTo(1));
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.IsDashboardVisible, Is.True);
                Assert.That(viewModel.HeaderTitleText, Is.EqualTo("desktop@example.test"));
                Assert.That(viewModel.HeaderStatusText, Is.EqualTo("Connected"));
                Assert.That(viewModel.ActionRequiredMessage, Is.Empty);
                Assert.That(controller.SignInRequest?.ServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
            });
        }


        [Test]
        public async Task SignOutCommand_ShowsNativeNotificationWhenSupported()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Documents", "Idle")));
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();

            await ExecuteAsync(viewModel.SignOutCommand);

            Assert.Multiple(() =>
            {
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Signed out"));
                Assert.That(notificationService.Notifications[0].Message, Is.EqualTo("Cotton Sync is signed out."));
            });
        }


        [Test]
        public async Task SessionRevoked_SignsOutAndShowsNativeNotificationWhenSupported()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(syncPairId, "Documents", "Syncing")));
            CollectingDesktopNotificationService notificationService = new CollectingDesktopNotificationService();
            using ShellViewModel viewModel = CreateViewModel(controller, notificationService: notificationService);
            await viewModel.InitializeAsync();
            viewModel.Password = "password";
            viewModel.TotpCode = "123456";
            await ExecuteAsync(viewModel.ShowSettingsCommand);

            controller.ReportSessionRevoked(new DesktopSessionRevocationSnapshot(DateTime.UtcNow));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.IsSetupVisible, Is.True);
                Assert.That(viewModel.AccountName, Is.EqualTo("Signed out"));
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Session expired"));
                Assert.That(viewModel.Password, Is.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
                Assert.That(viewModel.IsSettingsVisible, Is.False);
                Assert.That(viewModel.SyncPairs.Single().Status, Is.EqualTo("Idle"));
                Assert.That(viewModel.SignOutCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.Activities.First().Kind, Is.EqualTo("Account"));
                Assert.That(viewModel.Activities.First().Details, Is.EqualTo("Session revoked by server"));
                Assert.That(notificationService.Notifications, Has.Count.EqualTo(1));
                Assert.That(notificationService.Notifications[0].Title, Is.EqualTo("Session expired"));
                Assert.That(notificationService.Notifications[0].Message, Is.EqualTo("Sign in again to continue syncing."));
            });
        }
    }
}
