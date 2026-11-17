// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Auth;
using Cotton.Sdk;
using Cotton.Sync.Desktop.Auth;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    public partial class FileCottonTokenStoreTests
    {
        [TestCase(false)]
        [TestCase(true, IncludePlatform = "Linux")]
        public async Task CancelledRequest_PreservesRotatedSessionAcrossStoreRecreation(bool useLinuxSecretService)
        {
            string directory = CreateTempDirectory();
            try
            {
                string path = Path.Combine(directory, "tokens.json");
                FileCottonTokenStore store = useLinuxSecretService ? new FileCottonTokenStore(path) : CreateStore(path);
                await store.SaveAsync(new TokenPairDto { AccessToken = "expired-access", RefreshToken = "original-refresh" });
                using CancellationTokenSource cancellation = new();
                CancellingRefreshTokenStore cancellingStore = new(store, cancellation);
                using RotatingSessionHttpHandler handler = new();
                using HttpClient http = new(handler);
                await using CottonCloudClient client = new(http, cancellingStore,
                    new CottonSdkOptions { BaseAddress = new Uri("https://cotton.example.test") });
                Assert.CatchAsync<OperationCanceledException>(() => client.Auth.MeAsync(cancellation.Token));
                FileCottonTokenStore reopenedStore = useLinuxSecretService ? new FileCottonTokenStore(path) : CreateStore(path);
                TokenPairDto? persisted = await reopenedStore.GetAsync();
                Assert.That(persisted?.RefreshToken, Is.EqualTo("replacement-refresh"));
                await using CottonCloudClient restartedClient = new(http, reopenedStore,
                    new CottonSdkOptions { BaseAddress = new Uri("https://cotton.example.test") });
                Assert.That((await restartedClient.Auth.MeAsync()).Username, Is.EqualTo("account"));
                Assert.That(handler.RefreshCount, Is.EqualTo(1));
                await reopenedStore.ClearAsync();
            }
            finally
            {
                DeleteTempDirectory(directory);
            }
        }
    }
}
