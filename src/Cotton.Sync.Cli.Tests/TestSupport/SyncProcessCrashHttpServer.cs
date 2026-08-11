// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;

namespace Cotton.Sync.Cli.Tests.TestSupport
{
    internal class SyncProcessCrashHttpServer : SyncProcessCrashHttpServerBase
    {
        private const string ClientSettingsPath = "/api/v1/settings";
        private const string CreateFileFromChunksPath = "/api/v1/files/from-chunks";
        private readonly TaskCompletionSource _fileCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCreateResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _expectedContent;
        private readonly string _expectedContentHash;
        private readonly string _expectedRelativePath;
        private readonly Guid _ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private readonly Guid _remoteRootId;
        private bool _fileCreated;

        public SyncProcessCrashHttpServer(
            Guid remoteRootId,
            string expectedRelativePath,
            string expectedContentHash,
            byte[] expectedContent)
            : base("Crash-smoke HTTP server failed")
        {
            _remoteRootId = remoteRootId;
            _expectedRelativePath = expectedRelativePath;
            _expectedContentHash = expectedContentHash;
            _expectedContent = expectedContent;
            Start();
        }

        public Guid CreatedFileId { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public async Task WaitForFileCommittedAsync(TimeSpan timeout)
        {
            await _fileCommitted.Task.WaitAsync(timeout).ConfigureAwait(false);
        }

        public void ReleaseBlockedCreateResponse()
        {
            _releaseCreateResponse.TrySetResult();
        }

        protected override void ReleaseBlockedResponses()
        {
            _releaseCreateResponse.TrySetResult();
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

            if (await TryWriteUploadResponseAsync(response, request, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + request.PathAndQuery);
        }

        private async Task<bool> TryWriteUploadResponseAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            if (await TryWriteClientSettingsAsync(response, request, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await TryWriteChunkExistsAsync(response, request, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await TryWriteChunkUploadAsync(response, request, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return await TryWriteFileCreateAsync(response, request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryWriteClientSettingsAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            if (!IsRequest(request, HttpMethod.Get, ClientSettingsPath))
            {
                return false;
            }

            await WriteJsonAsync(response, HttpStatusCode.OK, new ClientSettingsDto
            {
                Version = "test",
                MaxChunkSizeBytes = 1024,
                SupportedHashAlgorithm = "SHA-256",
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> TryWriteChunkExistsAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            string path = "/api/v1/chunks/" + _expectedContentHash + "/exists";
            if (!IsRequest(request, HttpMethod.Get, path))
            {
                return false;
            }

            await WriteTextAsync(response, HttpStatusCode.OK, "false", cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> TryWriteChunkUploadAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            string path = "/api/v1/chunks/raw?hash=" + _expectedContentHash;
            if (!IsRequest(request, HttpMethod.Post, path))
            {
                return false;
            }

            if (!request.RawBody.SequenceEqual(_expectedContent))
            {
                throw new InvalidOperationException("Unexpected uploaded chunk content.");
            }

            await WriteTextAsync(response, HttpStatusCode.Created, string.Empty, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> TryWriteFileCreateAsync(
            HttpListenerResponse response,
            HttpRequestSnapshot request,
            CancellationToken cancellationToken)
        {
            if (!IsRequest(request, HttpMethod.Post, CreateFileFromChunksPath))
            {
                return false;
            }

            CreateFileFromChunksRequestDto createRequest = JsonSerializer.Deserialize<CreateFileFromChunksRequestDto>(
                request.Body,
                JsonOptions) ?? throw new InvalidOperationException("File-create request body is missing.");
            if (!IsExpectedFileCreateRequest(createRequest))
            {
                throw new InvalidOperationException("Unexpected file-create request.");
            }

            _fileCreated = true;
            _fileCommitted.TrySetResult();
            await _releaseCreateResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, HttpStatusCode.OK, CreateManifest(), cancellationToken).ConfigureAwait(false);
            return true;
        }

        private bool IsExpectedFileCreateRequest(CreateFileFromChunksRequestDto request)
        {
            return request.NodeId == _remoteRootId
                && string.Equals(request.Name, Path.GetFileName(_expectedRelativePath), StringComparison.Ordinal)
                && string.Equals(request.Hash, _expectedContentHash, StringComparison.Ordinal)
                && request.ChunkHashes.SequenceEqual([_expectedContentHash]);
        }

        private static bool IsRequest(HttpRequestSnapshot request, HttpMethod method, string path)
        {
            return request.Method == method && request.PathAndQuery == path;
        }

        private NodeContentDto CreateRootContent()
        {
            if (!_fileCreated)
            {
                return new NodeContentDto
                {
                    Id = _remoteRootId,
                    TotalCount = 0,
                };
            }

            return new NodeContentDto
            {
                Id = _remoteRootId,
                TotalCount = 1,
                Files = [CreateManifest()],
            };
        }

        private NodeFileManifestDto CreateManifest()
        {
            return new NodeFileManifestDto
            {
                Id = CreatedFileId,
                NodeId = _remoteRootId,
                FileManifestId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                OriginalNodeFileId = CreatedFileId,
                OwnerId = _ownerId,
                Name = Path.GetFileName(_expectedRelativePath),
                ContentType = "text/plain",
                SizeBytes = _expectedContent.Length,
                ContentHash = _expectedContentHash,
                ETag = "sha256-" + _expectedContentHash,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }
    }
}
