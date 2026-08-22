// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net.Http;
using System.Net.Sockets;
using Cotton.Auth;
using Cotton.Sdk.Auth;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Platform;

namespace Cotton.Sync.App.Tests.Auth
{
    public partial class AppCodeBrowserAuthFlowTests
    {
        [Test]
        public async Task SignInAsync_WaitsForPendingPollAndContinues()
        {
            List<TimeSpan> delays = new List<TimeSpan>();
            FakeCottonAuthClient authClient = new FakeCottonAuthClient();
            authClient.PollResults.Enqueue(new AppCodePollResult
            {
                Status = AppCodePollStatus.Pending,
                Error = "pending",
                RetryAfter = TimeSpan.FromSeconds(7),
            });
            authClient.PollResults.Enqueue(new AppCodePollResult
            {
                Status = AppCodePollStatus.Approved,
                Tokens = new TokenPairDto { AccessToken = "access", RefreshToken = "refresh" },
            });
            AppCodeBrowserAuthFlow flow = new AppCodeBrowserAuthFlow(
                authClient,
                new FakePlatformCommandService(),
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });

            await flow.SignInAsync(new AppCodeBrowserSignInRequest
            {
                ApplicationName = "Cotton Sync Desktop",
            });

            Assert.Multiple(() =>
            {
                Assert.That(authClient.PollCallCount, Is.EqualTo(2));
                Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(7) }));
            });
        }

        [Test]
        public async Task SignInAsync_RetriesTransientPollFailureAndContinues()
        {
            List<TimeSpan> delays = new List<TimeSpan>();
            FakeCottonAuthClient authClient = new FakeCottonAuthClient();
            authClient.PollExceptions.Enqueue(new HttpRequestException("Temporary network failure."));
            authClient.PollResults.Enqueue(new AppCodePollResult
            {
                Status = AppCodePollStatus.Approved,
                Tokens = new TokenPairDto { AccessToken = "access", RefreshToken = "refresh" },
            });
            AppCodeBrowserAuthFlow flow = new AppCodeBrowserAuthFlow(
                authClient,
                new FakePlatformCommandService(),
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });

            await flow.SignInAsync(new AppCodeBrowserSignInRequest
            {
                ApplicationName = "Cotton Sync Desktop",
            });

            Assert.Multiple(() =>
            {
                Assert.That(authClient.PollCallCount, Is.EqualTo(2));
                Assert.That(authClient.LastPollToken, Is.EqualTo(authClient.Session.PollToken));
                Assert.That(authClient.MeCallCount, Is.EqualTo(1));
                Assert.That(delays, Is.EqualTo(new[] { authClient.Session.PollInterval }));
            });
        }

        [Test]
        public async Task SignInAsync_RetriesTransientStartFailureAndContinues()
        {
            List<TimeSpan> delays = new List<TimeSpan>();
            FakeCottonAuthClient authClient = new FakeCottonAuthClient();
            authClient.StartExceptions.Enqueue(new HttpRequestException("Firewall blocked first request."));
            authClient.PollResults.Enqueue(new AppCodePollResult
            {
                Status = AppCodePollStatus.Approved,
                Tokens = new TokenPairDto { AccessToken = "access", RefreshToken = "refresh" },
            });
            FakePlatformCommandService platformCommands = new FakePlatformCommandService();
            AppCodeBrowserAuthFlow flow = new AppCodeBrowserAuthFlow(
                authClient,
                platformCommands,
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });

            AuthSession session = await flow.SignInAsync(new AppCodeBrowserSignInRequest
            {
                ApplicationName = "Cotton Sync Desktop",
            });

            Assert.Multiple(() =>
            {
                Assert.That(authClient.StartCallCount, Is.EqualTo(2));
                Assert.That(platformCommands.OpenWebCallCount, Is.EqualTo(1));
                Assert.That(authClient.PollCallCount, Is.EqualTo(1));
                Assert.That(session.Email, Is.EqualTo("browser@example.test"));
                Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1) }));
            });
        }

    }
}
