// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Http.Json;
using Cotton.Auth;
using Cotton.Sdk.Auth;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class DesktopShellShareLinkClientTests
    {
        [Test]
        public async Task CreateShareLinkAsync_ConvertsFileDownloadTokenToPublicShareUri()
        {
            Guid remoteFileId = Guid.NewGuid();
            var handler = new RecordingHttpHandler(request =>
            {
                Assert.That(request.RequestUri?.AbsolutePath, Is.EqualTo($"/base/api/v1/files/{remoteFileId:D}/download-link"));
                Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("access-token"));
                return JsonString("/api/v1/files/" + remoteFileId.ToString("D") + "/download?token=file-token");
            });
            var tokenStore = new MemoryCottonTokenStore("access-token", "refresh-token");
            var client = new DesktopShellShareLinkClient(
                new HttpClient(handler),
                tokenStore,
                new FakeAuthClient(tokenStore),
                new Uri("https://cloud.example/base"));

            DesktopShellShareLinkResult result = await client.CreateShareLinkAsync(new ShellShareLinkTarget(
                ShellShareLinkTargetStatus.Resolved,
                RemoteFileId: remoteFileId,
                Kind: ShellShareLinkTargetKind.File));

            Assert.Multiple(() =>
            {
                Assert.That(result.IsApiAvailable, Is.True);
                Assert.That(result.IsCreated, Is.True);
                Assert.That(result.ShareLink, Is.EqualTo("https://cloud.example/base/s/file-token"));
                Assert.That(result.FailureReason, Is.Null);
                Assert.That(handler.Requests, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task CreateShareLinkAsync_UsesFolderShareLink()
        {
            Guid remoteNodeId = Guid.NewGuid();
            var handler = new RecordingHttpHandler(request =>
            {
                Assert.That(request.RequestUri?.AbsolutePath, Is.EqualTo($"/api/v1/layouts/nodes/{remoteNodeId:D}/share-link"));
                return JsonString("/s/folder-token");
            });
            var tokenStore = new MemoryCottonTokenStore("access-token", "refresh-token");
            var client = new DesktopShellShareLinkClient(
                new HttpClient(handler),
                tokenStore,
                new FakeAuthClient(tokenStore),
                new Uri("https://cloud.example"));

            DesktopShellShareLinkResult result = await client.CreateShareLinkAsync(new ShellShareLinkTarget(
                ShellShareLinkTargetStatus.Resolved,
                RemoteNodeId: remoteNodeId,
                Kind: ShellShareLinkTargetKind.Directory));

            Assert.Multiple(() =>
            {
                Assert.That(result.IsCreated, Is.True);
                Assert.That(result.ShareLink, Is.EqualTo("https://cloud.example/s/folder-token"));
            });
        }

        [Test]
        public async Task CreateShareLinkAsync_RefreshesAndRetriesAfterUnauthorized()
        {
            Guid remoteFileId = Guid.NewGuid();
            var handler = new RecordingHttpHandler(request =>
            {
                if (request.Headers.Authorization?.Parameter == "expired-token")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("fresh-token"));
                return JsonString("/api/v1/files/" + remoteFileId.ToString("D") + "/download?token=fresh-file-token");
            });
            var tokenStore = new MemoryCottonTokenStore("expired-token", "refresh-token");
            var authClient = new FakeAuthClient(tokenStore)
            {
                RefreshedTokens = new TokenPairDto
                {
                    AccessToken = "fresh-token",
                    RefreshToken = "fresh-refresh-token",
                },
            };
            var client = new DesktopShellShareLinkClient(
                new HttpClient(handler),
                tokenStore,
                authClient,
                new Uri("https://cloud.example"));

            DesktopShellShareLinkResult result = await client.CreateShareLinkAsync(new ShellShareLinkTarget(
                ShellShareLinkTargetStatus.Resolved,
                RemoteFileId: remoteFileId,
                Kind: ShellShareLinkTargetKind.File));

            Assert.Multiple(() =>
            {
                Assert.That(result.IsCreated, Is.True);
                Assert.That(result.ShareLink, Is.EqualTo("https://cloud.example/s/fresh-file-token"));
                Assert.That(authClient.RefreshCalls, Is.EqualTo(1));
                Assert.That(handler.Requests, Has.Count.EqualTo(2));
            });
        }

        private static HttpResponseMessage JsonString(string value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value),
            };
        }

        private sealed class RecordingHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            public List<HttpRequestMessage> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests.Add(CloneRequest(request));
                return Task.FromResult(_handler(request));
            }

            private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
            {
                var clone = new HttpRequestMessage(request.Method, request.RequestUri);
                clone.Headers.Authorization = request.Headers.Authorization;
                return clone;
            }
        }

        private sealed class MemoryCottonTokenStore : ICottonTokenStore
        {
            private TokenPairDto? _tokens;

            public MemoryCottonTokenStore(string accessToken, string refreshToken)
            {
                _tokens = new TokenPairDto
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                };
            }

            public Task<TokenPairDto?> GetAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_tokens);
            }

            public Task SaveAsync(TokenPairDto tokens, CancellationToken cancellationToken = default)
            {
                _tokens = tokens;
                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                _tokens = null;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeAuthClient : ICottonAuthClient
        {
            private readonly ICottonTokenStore _tokenStore;

            public FakeAuthClient(ICottonTokenStore tokenStore)
            {
                _tokenStore = tokenStore;
            }

            public int RefreshCalls { get; private set; }

            public TokenPairDto RefreshedTokens { get; set; } = new()
            {
                AccessToken = "fresh-token",
                RefreshToken = "fresh-refresh-token",
            };

            public Task<TokenPairDto> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<AppCodeAuthorizationSession> StartAppCodeAsync(
                AppCodeStartRequestDto request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<AppCodePollResult> PollAppCodeAsync(
                string pollToken,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public async Task<TokenPairDto> RefreshAsync(
                string? refreshToken = null,
                CancellationToken cancellationToken = default)
            {
                RefreshCalls++;
                await _tokenStore.SaveAsync(RefreshedTokens, cancellationToken).ConfigureAwait(false);
                return RefreshedTokens;
            }

            public Task LogoutAsync(string? refreshToken = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<UserDto> MeAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
