// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal class LiveSyncBarrierHttpMessageHandler(
        LiveSyncManifestBarrier barrier,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            bool returned = false;
            try
            {
                await barrier.HoldResponseAsync(request, response, cancellationToken).ConfigureAwait(false);
                returned = true;
                return response;
            }
            finally
            {
                if (!returned)
                {
                    response.Dispose();
                }
            }
        }
    }
}
