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
        public async Task ServerProbe_NormalizesVerifiedBareHostAndEnablesSignIn()
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
            viewModel.Username = "desktop@example.test";
            viewModel.Password = "password";

            await WaitForAsync(() => viewModel.IsServerVerified);

            Assert.Multiple(() =>
            {
                Assert.That(controller.ProbedServerUrls, Is.EqualTo(new[] { "app.cottoncloud.dev" }));
                Assert.That(viewModel.ServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
                Assert.That(viewModel.IsServerProbeFailed, Is.False);
                Assert.That(viewModel.ServerProbeStatus, Is.EqualTo("Cotton Cloud"));
                Assert.That(viewModel.IsServerStepVisible, Is.False);
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
                Assert.That(viewModel.SetupTitle, Is.EqualTo("Sign in"));
                Assert.That(viewModel.SignInCommand.CanExecute(null), Is.True);
            });
        }


        [Test]
        public async Task ServerProbe_RetriesTransientNetworkFailureAndThenEnablesSignIn()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot());
            Queue<Exception> probeExceptions = new Queue<Exception>();
            probeExceptions.Enqueue(new HttpRequestException(
                "Firewall blocked the request.",
                new System.Net.Sockets.SocketException(10013)));
            controller.ServerProbeExceptionsByUrl["app.cottoncloud.dev"] = probeExceptions;
            controller.ServerProbeResultsByUrl["app.cottoncloud.dev"] = new DesktopServerProbeResult(
                new Uri("https://app.cottoncloud.dev/"),
                true,
                "Cotton Cloud",
                "instance-hash");
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.ServerUrl = "app.cottoncloud.dev";

            await WaitForAsync(() => viewModel.IsServerVerified);

            Assert.Multiple(() =>
            {
                Assert.That(controller.ProbedServerUrls, Is.EqualTo(new[]
                {
                    "app.cottoncloud.dev",
                    "app.cottoncloud.dev",
                }));
                Assert.That(viewModel.ServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
                Assert.That(viewModel.IsServerProbeFailed, Is.False);
                Assert.That(viewModel.ServerProbeStatus, Is.EqualTo("Cotton Cloud"));
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
            });
        }


        [Test]
        public async Task ServerProbe_ShowsNetworkFirewallMessageAfterTransientFailuresAreExhausted()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot());
            Queue<Exception> probeExceptions = new Queue<Exception>();
            for (int i = 0; i < 3; i++)
            {
                probeExceptions.Enqueue(new HttpRequestException(
                    "Firewall blocked the request.",
                    new System.Net.Sockets.SocketException(10013)));
            }

            controller.ServerProbeExceptionsByUrl["app.cottoncloud.dev"] = probeExceptions;
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.ServerUrl = "app.cottoncloud.dev";

            await WaitForAsync(() => viewModel.IsServerProbeFailed, attempts: 250);

            Assert.Multiple(() =>
            {
                Assert.That(controller.ProbedServerUrls, Is.EqualTo(new[]
                {
                    "app.cottoncloud.dev",
                    "app.cottoncloud.dev",
                    "app.cottoncloud.dev",
                }));
                Assert.That(viewModel.IsServerVerified, Is.False);
                Assert.That(viewModel.ServerProbeStatus, Is.EqualTo("Cannot reach server. Check network or firewall."));
                Assert.That(viewModel.IsServerStepVisible, Is.True);
                Assert.That(viewModel.IsSignInStepVisible, Is.False);
            });
        }


        [Test]
        public async Task ServerProbe_IgnoresStaleFailureAfterServerUrlChanges()
        {
            TaskCompletionSource<DesktopServerProbeResult> staleProbe = new TaskCompletionSource<DesktopServerProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                IgnoreServerProbeCancellation = true,
            };
            controller.ServerProbeCompletionsByUrl["first.cottoncloud.dev"] = staleProbe;
            controller.ServerProbeResultsByUrl["app.cottoncloud.dev"] = new DesktopServerProbeResult(
                new Uri("https://app.cottoncloud.dev/"),
                true,
                "Cotton Cloud",
                "instance-hash");
            using ShellViewModel viewModel = CreateViewModel(controller);
            await viewModel.InitializeAsync();

            viewModel.ServerUrl = "first.cottoncloud.dev";
            await WaitForAsync(() => controller.ProbedServerUrls.Contains("first.cottoncloud.dev"));
            viewModel.ServerUrl = "app.cottoncloud.dev";
            await WaitForAsync(() => viewModel.IsServerVerified);

            staleProbe.SetException(new System.Net.Http.HttpRequestException("stale probe failed"));
            await Task.Delay(50);

            Assert.Multiple(() =>
            {
                Assert.That(controller.ProbedServerUrls, Is.EqualTo(new[]
                {
                    "first.cottoncloud.dev",
                    "app.cottoncloud.dev",
                }));
                Assert.That(viewModel.ServerUrl, Is.EqualTo("https://app.cottoncloud.dev/"));
                Assert.That(viewModel.IsServerVerified, Is.True);
                Assert.That(viewModel.IsServerProbeFailed, Is.False);
                Assert.That(viewModel.ServerProbeStatus, Is.EqualTo("Cotton Cloud"));
                Assert.That(viewModel.IsSignInStepVisible, Is.True);
            });
        }


        [Test]
        public async Task SetupFlow_StartsWithServerStepUntilCottonServerIsVerified()
        {
            using ShellViewModel viewModel = CreateViewModel(new FakeDesktopShellController(CreateSignedOutSnapshot()));
            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSetupVisible, Is.True);
                Assert.That(viewModel.IsServerStepVisible, Is.True);
                Assert.That(viewModel.IsSignInStepVisible, Is.False);
                Assert.That(viewModel.SetupTitle, Is.EqualTo("Connect Cotton Sync"));
                Assert.That(viewModel.SignInCommand.CanExecute(null), Is.False);
            });
        }


        [Test]
        public async Task InitializeAsync_ShowsStartupLoadingInsteadOfSetupWhileRestoringSession()
        {
            TaskCompletionSource<bool> loadCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedInSnapshot(CreatePair(Guid.NewGuid(), "Cloud", "Idle")))
            {
                LoadCompletion = loadCompletion,
            };
            using ShellViewModel viewModel = CreateViewModel(controller);

            Task initializeTask = viewModel.InitializeAsync();
            await WaitForAsync(() => controller.LoadStarted);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsStartupLoadingVisible, Is.True);
                Assert.That(viewModel.IsSetupVisible, Is.False);
                Assert.That(viewModel.IsServerStepVisible, Is.False);
                Assert.That(viewModel.IsSignInStepVisible, Is.False);
                Assert.That(viewModel.IsDashboardVisible, Is.False);
            });

            loadCompletion.SetResult(true);
            await initializeTask;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsStartupLoadingVisible, Is.False);
                Assert.That(viewModel.IsSignedIn, Is.True);
                Assert.That(viewModel.IsSetupVisible, Is.False);
                Assert.That(viewModel.IsDashboardVisible, Is.True);
            });
        }


        [Test]
        public async Task InitializeAsync_WhenLoadFailsBeforeSignInStepShowsActionRequired()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                LoadException = new InvalidOperationException("Preferences database is unavailable."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.IsServerStepVisible, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(viewModel.ActionRequiredMessage, Is.EqualTo("Preferences database is unavailable."));
                Assert.That(viewModel.CurrentProgressText, Is.EqualTo("Sign in to continue."));
            });
        }


        [Test]
        public async Task InitializeAsync_WhenLocalDatabaseIsCorruptShowsRepairGuidance()
        {
            FakeDesktopShellController controller = new FakeDesktopShellController(CreateSignedOutSnapshot())
            {
                LoadException = new InvalidOperationException("SQLite Error 26: 'file is not a database'."),
            };
            using ShellViewModel viewModel = CreateViewModel(controller);

            await viewModel.InitializeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsSignedIn, Is.False);
                Assert.That(viewModel.IsServerStepVisible, Is.True);
                Assert.That(viewModel.GlobalStatus, Is.EqualTo("Action required"));
                Assert.That(
                    viewModel.ActionRequiredMessage,
                    Is.EqualTo("Local Cotton Sync state appears to be corrupt. Export diagnostics, then reset the local app data or choose a fresh data directory and sign in again."));
                Assert.That(viewModel.ActionRequiredMessage, Does.Not.Contain("SQLite Error"));
            });
        }


        [Test]
        public async Task ChangeServerCommand_ReturnsSetupFlowToServerStepAndClearsSecrets()
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
            viewModel.Password = "password";
            viewModel.TotpCode = "123456";
            await WaitForAsync(() => viewModel.IsSignInStepVisible);

            await ExecuteAsync(viewModel.ChangeServerCommand);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IsServerVerified, Is.False);
                Assert.That(viewModel.IsServerStepVisible, Is.True);
                Assert.That(viewModel.IsSignInStepVisible, Is.False);
                Assert.That(viewModel.Password, Is.Empty);
                Assert.That(viewModel.TotpCode, Is.Empty);
                Assert.That(viewModel.ServerProbeStatus, Is.EqualTo("Edit server address"));
            });
        }
    }
}
