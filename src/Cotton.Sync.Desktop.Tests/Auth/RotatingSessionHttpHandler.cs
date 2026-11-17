// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Http.Json;

namespace Cotton.Sync.Desktop.Tests.Auth
{
    internal class RotatingSessionHttpHandler : HttpMessageHandler
    {
        public int RefreshCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/me")
            {
                if (request.Headers.Authorization?.Parameter == "replacement-access")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { id = Guid.NewGuid(), username = "account" }),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (request.RequestUri.AbsolutePath == "/api/v1/auth/refresh")
            {
                RefreshCount++;
                return Task.FromResult(new HttpResponseMessage(RefreshCount == 1 ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                {
                    Content = JsonContent.Create(new { accessToken = "replacement-access", refreshToken = "replacement-refresh" }),
                });
            }

            throw new InvalidOperationException("Unexpected session request.");
        }
    }
}
