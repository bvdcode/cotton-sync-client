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
        private class RemoteRootNotFoundServerHandler : HttpMessageHandler
        {
            private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
            private readonly Guid _remoteRootId;

            public RemoteRootNotFoundServerHandler(Guid remoteRootId)
            {
                _remoteRootId = remoteRootId;
            }

            public List<HttpRequestSnapshot> Requests { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                byte[] rawBody = request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                string body = Encoding.UTF8.GetString(rawBody);
                HttpRequestSnapshot snapshot = new HttpRequestSnapshot(
                    request.Method,
                    request.RequestUri?.PathAndQuery ?? string.Empty,
                    request.Headers.Authorization?.Parameter,
                    body,
                    rawBody);
                Requests.Add(snapshot);

                if (snapshot.Method == HttpMethod.Post && snapshot.PathAndQuery == "/api/v1/auth/login")
                {
                    return Json(HttpStatusCode.OK, new TokenPairDto
                    {
                        AccessToken = "access-token",
                        RefreshToken = "refresh-token",
                    });
                }

                if (snapshot.Method == HttpMethod.Get
                    && snapshot.PathAndQuery == "/api/v1/layouts/nodes/" + _remoteRootId.ToString("D"))
                {
                    Assert.That(snapshot.AuthorizationParameter, Is.EqualTo("access-token"));
                    return Json(HttpStatusCode.NotFound, new
                    {
                        success = false,
                        message = "Remote folder was not found.",
                    });
                }

                if (snapshot.Method == HttpMethod.Post && snapshot.PathAndQuery == "/api/v1/auth/logout?refreshToken=refresh-token")
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Unexpected request: " + snapshot.PathAndQuery),
                };
            }

            private static HttpResponseMessage Json(HttpStatusCode statusCode, object payload)
            {
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions),
                        Encoding.UTF8,
                        "application/json"),
                };
            }
        }
    }
}
