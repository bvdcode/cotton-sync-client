// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Text;
using System.Text.Json;
using Cotton.Auth;

namespace Cotton.Sync.Cli.Tests.TestSupport
{
    internal class AppCodeAuthServerHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly bool _alwaysPending;
        private readonly bool _deny;
        private readonly Exception? _startException;
        private int _pollCount;

        public AppCodeAuthServerHandler(
            bool deny = false,
            bool alwaysPending = false,
            Exception? startException = null)
        {
            _deny = deny;
            _alwaysPending = alwaysPending;
            _startException = startException;
        }

        public List<HttpRequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpRequestSnapshot snapshot = await CaptureRequestAsync(request, cancellationToken).ConfigureAwait(false);
            Requests.Add(snapshot);
            return CreateResponse(snapshot);
        }

        private static async Task<HttpRequestSnapshot> CaptureRequestAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] rawBody = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new HttpRequestSnapshot(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.Parameter,
                Encoding.UTF8.GetString(rawBody),
                rawBody);
        }

        private HttpResponseMessage CreateResponse(HttpRequestSnapshot request)
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/oauth/app-code/start")
            {
                return CreateStartResponse();
            }

            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/oauth/app-code/poll")
            {
                return CreatePollResponse(request);
            }

            if (request.Method == HttpMethod.Get && request.PathAndQuery == "/api/v1/auth/me")
            {
                return CreateCurrentUserResponse(request);
            }

            if (request.Method == HttpMethod.Post
                && request.PathAndQuery == "/api/v1/auth/logout?refreshToken=refresh-token")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Unexpected request: " + request.PathAndQuery),
            };
        }

        private HttpResponseMessage CreateStartResponse()
        {
            if (_startException is not null)
            {
                throw _startException;
            }

            return Json(HttpStatusCode.OK, new
            {
                approvalId = Guid.Parse("0190a000-0000-7000-8000-000000000022"),
                approvalUrl = "/oauth/app-code/0190a000-0000-7000-8000-000000000022",
                pollToken = "poll-token",
                expiresAt = DateTime.UtcNow.AddMinutes(10),
                pollIntervalSeconds = 1,
            });
        }

        private HttpResponseMessage CreatePollResponse(HttpRequestSnapshot request)
        {
            Assert.That(request.Body, Does.Contain("\"pollToken\":\"poll-token\""));
            _pollCount++;
            if (_alwaysPending)
            {
                return Json(HttpStatusCode.Accepted, new { error = "pending", retryAfterSeconds = 1 });
            }

            if (_deny)
            {
                return Json(HttpStatusCode.Forbidden, new { error = "denied" });
            }

            return Json(HttpStatusCode.OK, new { accessToken = "access-token", refreshToken = "refresh-token" });
        }

        private HttpResponseMessage CreateCurrentUserResponse(HttpRequestSnapshot request)
        {
            Assert.That(request.AuthorizationParameter, Is.EqualTo("access-token"));
            Assert.That(_pollCount, Is.EqualTo(1));
            return Json(HttpStatusCode.OK, new UserDto
            {
                Id = Guid.Parse("0190a000-0000-7000-8000-000000000023"),
                Username = "browser",
                Email = "browser@example.test",
            });
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
