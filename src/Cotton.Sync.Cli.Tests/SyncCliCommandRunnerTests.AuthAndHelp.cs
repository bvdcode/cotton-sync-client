// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Sync;
using Cotton.Sync.Cli;
using Cotton.Sync.Cli.Tests.TestSupport;
using Cotton.Sync.State;

namespace Cotton.Sync.Cli.Tests
{
    public partial class SyncCliCommandRunnerTests
    {
        [Test]
        public async Task RunAsync_PrintsHelpForEmptyArguments()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync([], output, error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(output.ToString(), Does.Contain("state-summary"));
                Assert.That(output.ToString(), Does.Contain("auth-browser"));
                Assert.That(output.ToString(), Does.Contain("sync-once"));
                Assert.That(output.ToString(), Does.Contain("sync-soak"));
                Assert.That(output.ToString(), Does.Contain("sync-crud-smoke"));
                Assert.That(error.ToString(), Is.Empty);
            });
        }

        [Test]
        public async Task Program_MapsOperationCanceledExceptionToStableExitCode()
        {
            using StringWriter error = new();

            int exitCode = await Program.RunWithTopLevelExceptionMappingAsync(
                () => Task.FromException<int>(new OperationCanceledException("stop requested")),
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(130));
                Assert.That(error.ToString(), Does.Contain("Operation canceled."));
                Assert.That(error.ToString(), Does.Not.Contain("OperationCanceledException"));
                Assert.That(error.ToString(), Does.Not.Contain(" at "));
            });
        }

        [Test]
        public async Task Program_MapsUnexpectedExceptionToStableError()
        {
            using StringWriter error = new();

            int exitCode = await Program.RunWithTopLevelExceptionMappingAsync(
                () => Task.FromException<int>(new InvalidOperationException("boom")),
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(error.ToString(), Does.Contain("Unexpected error: InvalidOperationException: boom"));
                Assert.That(error.ToString(), Does.Not.Contain(" at "));
            });
        }

        [Test]
        public async Task SyncOnceSuccess_PrintsTotalActivitiesWhenRetainedActivityListIsTruncated()
        {
            using StringWriter output = new StringWriter();
            SyncRunResult result = new SyncRunResult();
            result.RecordActivity(
                new SyncActivity
                {
                    Kind = SyncActivityKind.PlaceholderCreated,
                    RelativePath = "Cloud/file-0001.txt",
                },
                maximumStoredActivities: 1);
            result.RecordActivity(
                new SyncActivity
                {
                    Kind = SyncActivityKind.PlaceholderCreated,
                    RelativePath = "Cloud/file-0002.txt",
                },
                maximumStoredActivities: 1);
            SyncCliConnectionOptions options = new SyncCliConnectionOptions(
                new Uri("https://cotton.test/"),
                "testuser",
                "testpassword",
                "C:\\Sync",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                null,
                "pair-a",
                "sync-state.db",
                null,
                UseBrowserLogin: false);
            SyncCliPassResult pass = new SyncCliPassResult(result, []);

            await SyncCliCommandRunner.WriteSyncOnceSuccessAsync(output, options, pass);

            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("Activities: 2"));
                Assert.That(text, Does.Contain("Retained activities: 1"));
                Assert.That(text, Does.Contain("PlaceholderCreated Cloud/file-0001.txt"));
                Assert.That(text, Does.Not.Contain("Activities: 1"));
            });
        }

        [Test]
        public async Task AuthBrowser_PrintsApprovalUrlAndSignedInAccount()
        {
            AppCodeAuthServerHandler handler = new AppCodeAuthServerHandler();
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "auth-browser",
                    "--server",
                    "cotton.test",
                    "--application-version",
                    "1.2.3",
                    "--device-name",
                    "workstation",
                ],
                output,
                error,
                httpClient);

            string text = output.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(error.ToString(), Is.Empty);
                Assert.That(text, Does.Contain("Cotton Sync browser sign-in"));
                Assert.That(text, Does.Contain("Approval URL: https://cotton.test/oauth/app-code/0190a000-0000-7000-8000-000000000022"));
                Assert.That(text, Does.Contain("Open this URL in your browser to approve sign-in."));
                Assert.That(text, Does.Contain("Waiting for browser approval..."));
                Assert.That(text, Does.Contain("Signed in: browser@example.test"));
                Assert.That(text, Does.Contain("Signed out."));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/poll",
                    "/api/v1/auth/me",
                    "/api/v1/auth/logout?refreshToken=refresh-token",
                }));
                Assert.That(handler.Requests[0].Body, Does.Contain("\"applicationName\":\"Cotton Sync CLI\""));
                Assert.That(handler.Requests[0].Body, Does.Contain("\"applicationVersion\":\"1.2.3\""));
                Assert.That(handler.Requests[0].Body, Does.Contain("\"deviceName\":\"workstation\""));
            });
        }

        [Test]
        public async Task AuthBrowser_ReturnsFailureForDeniedApproval()
        {
            AppCodeAuthServerHandler handler = new AppCodeAuthServerHandler(deny: true);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "auth-browser",
                    "--server",
                    "https://cotton.test/",
                ],
                output,
                error,
                httpClient);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Approval URL: https://cotton.test/oauth/app-code/0190a000-0000-7000-8000-000000000022"));
                Assert.That(error.ToString(), Does.Contain("Browser sign-in was denied."));
                Assert.That(error.ToString(), Does.Contain("denied"));
                Assert.That(ReadStartRequestProperty(handler.Requests[0].Body, "applicationVersion"), Is.EqualTo(SyncCliAppVersion.Current));
                Assert.That(handler.Requests.Select(static request => request.PathAndQuery), Is.EqualTo(new[]
                {
                    "/api/v1/oauth/app-code/start",
                    "/api/v1/oauth/app-code/poll",
                }));
            });
        }

        [Test]
        public async Task AuthBrowser_DefaultStartRequestUsesCliVersion()
        {
            AppCodeAuthServerHandler handler = new AppCodeAuthServerHandler(deny: true);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "auth-browser",
                    "--server",
                    "https://cotton.test/",
                ],
                output,
                error,
                httpClient);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(ReadStartRequestProperty(handler.Requests[0].Body, "applicationName"), Is.EqualTo("Cotton Sync CLI"));
                Assert.That(ReadStartRequestProperty(handler.Requests[0].Body, "applicationVersion"), Is.EqualTo(SyncCliAppVersion.Current));
                Assert.That(ReadStartRequestProperty(handler.Requests[0].Body, "deviceName"), Is.EqualTo("Cotton Sync CLI"));
                Assert.That(handler.Requests[0].Body, Does.Not.Contain("Unknown version"));
            });
        }

        [Test]
        public void SyncCliAppVersion_CurrentDoesNotExposeBuildMetadata()
        {
            Assert.That(SyncCliAppVersion.Current, Does.Not.Contain("+"));
            Assert.That(SyncCliAppVersion.Current, Is.Not.EqualTo("unknown"));
        }

        [Test]
        public async Task RunAsync_PrintsVersionForVersionFlag()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(["--version"], output, error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(output.ToString().Trim(), Is.EqualTo(SyncCliAppVersion.Current));
                Assert.That(error.ToString(), Is.Empty);
            });
        }

        [Test]
        public async Task AuthBrowser_ReturnsFailureWhenApprovalTimesOut()
        {
            AppCodeAuthServerHandler handler = new AppCodeAuthServerHandler(alwaysPending: true);
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "auth-browser",
                    "--server",
                    "https://cotton.test/",
                    "--timeout-seconds",
                    "1",
                ],
                output,
                error,
                httpClient);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Open this URL in your browser to approve sign-in."));
                Assert.That(output.ToString(), Does.Contain("Waiting for browser approval..."));
                Assert.That(error.ToString(), Does.Contain("Browser sign-in timed out"));
            });
        }

        [Test]
        public async Task AuthBrowser_ReturnsErrorForInvalidTimeout()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "auth-browser",
                    "--server",
                    "https://cotton.test/",
                    "--timeout-seconds",
                    "0",
                ],
                output,
                error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--timeout-seconds must be a positive integer."));
            });
        }

        [Test]
        public async Task AuthBrowser_ReturnsFailureWithoutStackTraceWhenStartNetworkFails()
        {
            AppCodeAuthServerHandler handler = new AppCodeAuthServerHandler(startException: new HttpRequestException("firewall blocked"));
            using HttpClient httpClient = new HttpClient(handler);
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(
                [
                    "auth-browser",
                    "--server",
                    "https://cotton.test/",
                ],
                output,
                error,
                httpClient);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.ToString(), Does.Contain("Cotton Sync browser sign-in"));
                Assert.That(output.ToString(), Does.Not.Contain("Approval URL:"));
                Assert.That(error.ToString(), Does.Contain("Check network or firewall"));
                Assert.That(error.ToString(), Does.Not.Contain("HttpRequestException"));
                Assert.That(handler.Requests, Has.Count.EqualTo(3));
            });
        }

        [Test]
        public async Task AuthBrowser_HelpMentionsTimeout()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync([], output, error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(output.ToString(), Does.Contain("--timeout-seconds"));
            });
        }

        [Test]
        public async Task AuthBrowser_ReturnsErrorForMissingServer()
        {
            using StringWriter output = new StringWriter();
            using StringWriter error = new StringWriter();

            int exitCode = await SyncCliCommandRunner.RunAsync(["auth-browser"], output, error);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.ToString(), Is.Empty);
                Assert.That(error.ToString(), Does.Contain("--server"));
            });
        }
    }
}
