// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Live
{
    public class LiveTransferGateTests
    {
        [Test]
        public async Task BeforeSuccessfulCompletion_AllowsRetryAfterFailedRequest()
        {
            LiveTransferGate gate = new();
            gate.Arm("/chunks/raw");
            using HttpRequestMessage first = new(HttpMethod.Post, "https://example.test/chunks/raw");
            using HttpRequestMessage retry = new(HttpMethod.Post, "https://example.test/chunks/raw");
            try
            {
                await gate.HoldAsync(first, CancellationToken.None);
                Task retried = gate.HoldAsync(retry, CancellationToken.None);

                Assert.That(retried.IsCompletedSuccessfully, Is.True,
                    "A failed first request must leave retries free to complete a chunk.");
                await retried;
            }
            finally
            {
                gate.Release();
            }
        }

        [Test]
        public async Task AfterSuccessfulCompletion_BlocksNextChunkUntilReleased()
        {
            LiveTransferGate gate = new();
            gate.Arm("/chunks/raw");
            using HttpRequestMessage first = new(HttpMethod.Post, "https://example.test/chunks/raw");
            using HttpRequestMessage next = new(HttpMethod.Post, "https://example.test/chunks/raw");
            try
            {
                await gate.HoldAsync(first, CancellationToken.None);
                gate.Completed(first);
                Task held = gate.HoldAsync(next, CancellationToken.None);

                await gate.WaitUntilBlockedAsync(CancellationToken.None);
                Assert.That(held.IsCompleted, Is.False);
                gate.Release();
                await held;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
