// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Auth
{
    internal class CountingSessionHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        private int _refreshCount;
        public int RefreshCount => Volatile.Read(ref _refreshCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/auth/refresh")
            {
                Interlocked.Increment(ref _refreshCount);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
