// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Auth;
using Cotton.Sdk;
using Cotton.Sdk.Auth;

namespace Cotton.Sync.Desktop.Tests.Live
{
    public class LiveTestDataCleanupTests
    {
        private LiveTestDataCleanupHandler _handler = null!;
        private HttpClient _http = null!;
        private CottonCloudClient _client = null!;
        private LiveTestDataCleanup _cleanup = null!;

        [SetUp]
        public async Task SetUp()
        {
            _handler = new LiveTestDataCleanupHandler();
            _http = new HttpClient(_handler);
            InMemoryCottonTokenStore tokens = new();
            await tokens.SaveAsync(new TokenPairDto { AccessToken = "test-access", RefreshToken = "test-refresh" });
            Uri server = new("https://sync-test.invalid");
            _client = new CottonCloudClient(_http, tokens, new CottonSdkOptions { BaseAddress = server });
            _cleanup = new LiveTestDataCleanup(_client, _http, tokens, server);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _client.DisposeAsync();
            _http.Dispose();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClearAsync_RemovesOnlyOwnedNamespacesAndTheirTrashAcrossPages(bool abandoned)
        {
            if (abandoned)
            {
                await _cleanup.ClearAbandonedAsync(CancellationToken.None);
            }
            else
            {
                await _cleanup.ClearAsync(_handler.FirstNamespaceId, CancellationToken.None);
            }

            List<string> expected =
            [
                $"/api/v1/files/{_handler.FirstFileId}?skipTrash=true",
                $"/api/v1/layouts/nodes/{_handler.FirstNamespaceId}?skipTrash=true",
            ];
            if (abandoned)
            {
                expected.Add($"/api/v1/files/{_handler.SecondFileId}?skipTrash=true");
                expected.Add($"/api/v1/layouts/nodes/{_handler.SecondNamespaceId}?skipTrash=true");
            }
            Assert.That(_handler.Deletes, Is.EquivalentTo(expected));
            Assert.That(_handler.Reads.Any(path => path.Contains("page=2&", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task ClearAbandonedAsync_RefusesPrivateServerWithoutDeletingAnything()
        {
            _handler.IsPublic = false;
            Assert.ThrowsAsync<InvalidOperationException>(() => _cleanup.ClearAbandonedAsync(CancellationToken.None));
            Assert.That(_handler.Deletes, Is.Empty);
        }

        [Test]
        public async Task RequireAvailableBytesAsync_FailsBeforeTransferWhenQuotaIsInsufficient()
        {
            await _cleanup.RequireAvailableBytesAsync(100, CancellationToken.None);
            Assert.ThrowsAsync<InvalidOperationException>(() => _cleanup.RequireAvailableBytesAsync(101, CancellationToken.None));
            Assert.That(_handler.Deletes, Is.Empty);
        }
    }
}
