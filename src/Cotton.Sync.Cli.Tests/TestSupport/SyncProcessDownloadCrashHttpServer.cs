// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;

namespace Cotton.Sync.Cli.Tests.TestSupport
{
    internal class SyncProcessDownloadCrashHttpServer : SyncProcessCrashHttpServerBase
    {
        private readonly byte[] _content;
        private readonly TaskCompletionSource _firstDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _contentHash;
        private readonly string _relativePath;
        private readonly Guid _ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private readonly Guid _remoteRootId;
        private bool _firstDownloadWasBlocked;

        public SyncProcessDownloadCrashHttpServer(
            Guid remoteRootId,
            string relativePath,
            string contentHash,
            byte[] content)
            : base("Download crash-smoke HTTP server failed")
        {
            _remoteRootId = remoteRootId;
            _relativePath = relativePath;
            _contentHash = contentHash;
            _content = content;
            Start();
        }

        public Guid RemoteFileId { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public async Task WaitForFirstDownloadStartedAsync(TimeSpan timeout)
        {
            await _firstDownloadStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
        }

        public void ReleaseFirstDownload()
        {
            _releaseFirstDownload.TrySetResult();
        }

        protected override void ReleaseBlockedResponses()
        {
            _releaseFirstDownload.TrySetResult();
        }

        protected override async Task WriteResponseAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            if (await TryWriteCommonResponseAsync(
                    response,
                    request,
                    _remoteRootId,
                    CreateRootContent(),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            if (request.Method == HttpMethod.Get
                && request.PathAndQuery == "/api/v1/files/" + RemoteFileId.ToString("D") + "/content-manifest")
            {
                await WriteJsonAsync(response, HttpStatusCode.OK,
                    SyncTestContentManifestFactory.Create(RemoteFileId, _content, _content.Length / 2),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.Method == HttpMethod.Get
                && request.PathAndQuery == "/api/v1/files/" + RemoteFileId.ToString("D") + "/content?download=false")
            {
                Assert.That(request.GetHeader("If-Match"), Is.EqualTo("\"sha256-" + _contentHash + "\""));
                await WriteContentAsync(response, request, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + request.PathAndQuery);
        }

        private NodeContentDto CreateRootContent()
        {
            return new NodeContentDto
            {
                Id = _remoteRootId,
                Files = [CreateManifest()],
            };
        }

        private async Task WriteContentAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            int split = _content.Length / 2;
            string? range = request.GetHeader("Range");
            int offset = range switch
            {
                string first when first == $"bytes=0-{split - 1}" => 0,
                string second when second == $"bytes={split}-{_content.Length - 1}" => split,
                _ => throw new InvalidOperationException("Unexpected download range: " + range),
            };
            int length = offset == 0 ? split : _content.Length - split;
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.ContentType = "text/plain";
            response.ContentLength64 = length;
            response.Headers["Content-Range"] = $"bytes {offset}-{offset + length - 1}/{_content.Length}";
            response.Headers["ETag"] = "\"sha256-" + _contentHash + "\"";
            if (offset == split && !_firstDownloadWasBlocked)
            {
                _firstDownloadWasBlocked = true;
                int partialLength = Math.Max(1, length / 2);
                await response.OutputStream.WriteAsync(_content.AsMemory(offset, partialLength), cancellationToken).ConfigureAwait(false);
                await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _firstDownloadStarted.TrySetResult();
                await _releaseFirstDownload.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await response.OutputStream.WriteAsync(
                    _content.AsMemory(offset + partialLength, length - partialLength),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await response.OutputStream.WriteAsync(_content.AsMemory(offset, length), cancellationToken).ConfigureAwait(false);
        }

        private NodeFileManifestDto CreateManifest()
        {
            return new NodeFileManifestDto
            {
                Id = RemoteFileId,
                NodeId = _remoteRootId,
                FileManifestId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                OriginalNodeFileId = RemoteFileId,
                OwnerId = _ownerId,
                Name = Path.GetFileName(_relativePath),
                ContentType = "text/plain",
                SizeBytes = _content.Length,
                ContentHash = _contentHash,
                ETag = "sha256-" + _contentHash,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }
    }
}
