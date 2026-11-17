// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Auth;
using Cotton.Sdk;
using Cotton.Sync.Desktop.Auth;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    [Platform("Linux")]
    public class LinuxLiveSessionTests
    {
        [Test, Category("LiveServer")]
        public async Task RefreshCancellationAndConcurrentUnauthorizedRequestsPreserveSessionAcrossRestart()
        {
            if (Environment.GetEnvironmentVariable("COTTON_SYNC_LIVE_SESSION_TEST") != "1")
            {
                Assert.Ignore("Live session testing must be explicitly enabled with a dedicated account.");
            }

            string server = Environment.GetEnvironmentVariable("COTTON_SYNC_QA_SERVER")
                ?? throw new InvalidOperationException("A test server is required.");
            string username = Environment.GetEnvironmentVariable("COTTON_SYNC_QA_USERNAME")
                ?? throw new InvalidOperationException("A dedicated test username is required.");
            string password = Environment.GetEnvironmentVariable("COTTON_SYNC_QA_PASSWORD")
                ?? throw new InvalidOperationException("A dedicated test password is required.");
            string directory = Path.Combine(Path.GetTempPath(), "cotton-linux-session-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "tokens.json");
            CottonSdkOptions options = new() { BaseAddress = new Uri(server) };
            FileCottonTokenStore store = new(path);
            using CountingSessionHandler handler = new(new HttpClientHandler());
            using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
            await using CottonCloudClient client = new(http, store, options);
            try
            {
                await client.Auth.LoginAsync(new LoginRequestDto { Username = username, Password = password });
                using CancellationTokenSource cancellation = new();
                CancellingRefreshTokenStore cancellingStore = new(store, cancellation);
                await using CottonCloudClient cancelledClient = new(http, cancellingStore, options);
                Assert.CatchAsync<OperationCanceledException>(() =>
                    cancelledClient.Auth.RefreshAsync(cancellationToken: cancellation.Token));
                FileCottonTokenStore reopened = new(path);
                await using CottonCloudClient restarted = new(http, reopened, options);
                Assert.That((await restarted.Auth.MeAsync()).Username, Is.EqualTo(username));
                TokenPairDto tokens = await reopened.GetAsync()
                    ?? throw new InvalidOperationException("The rotated session was not saved.");
                tokens.AccessToken = "expired-acceptance-token";
                await reopened.SaveAsync(tokens);
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => restarted.Auth.MeAsync()));
                Assert.That(handler.RefreshCount, Is.EqualTo(2));
                Assert.That((await new FileCottonTokenStore(path).GetAsync())?.AccessToken,
                    Is.Not.EqualTo("expired-acceptance-token"));
                TestContext.Out.WriteLine("Live Linux session: cancellation, eight concurrent 401 responses, rotation and restart passed.");
            }
            finally
            {
                await client.Auth.LogoutAsync();
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
