// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal class LiveTransferHandler(LiveTransferGate gate, HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.HoldAsync(request, cancellationToken);
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                gate.Completed(request);
            }
            return response;
        }
    }
}
