// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;
using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Chunks;
using Cotton.Sdk.Files;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Notifications;
using Cotton.Sdk.Realtime;
using Cotton.Sdk.Settings;
using Cotton.Sdk.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        private class FakeFileClient : ICottonFileClient
        {
            private int _activeChunkDownloads;
            private int _maxActiveChunkDownloads;

            public Dictionary<Guid, NodeFileManifestDto> Files { get; } = [];

            public List<CreateFileFromChunksRequestDto> CreateRequests { get; } = [];

            public List<(Guid NodeFileId, CreateFileFromChunksRequestDto Request, string? ExpectedETag)> UpdateRequests { get; } = [];

            public List<(Guid NodeFileId, Guid ParentId, string? ExpectedETag)> MoveRequests { get; } = [];

            public List<(Guid NodeFileId, string Name, string? ExpectedETag)> RenameRequests { get; } = [];

            public List<(Guid NodeFileId, bool SkipTrash, string? ExpectedETag)> Deletes { get; } = [];

            public Dictionary<Guid, byte[]> Downloads { get; } = [];

            public List<(Guid NodeFileId, long Offset, long Length, string? ExpectedETag)> RangeDownloads { get; } = [];

            public List<(Guid NodeFileId, int ChunkNumber, string? ExpectedETag)> ChunkDownloads { get; } = [];

            public int DownloadChunkSizeBytes { get; set; } = int.MaxValue;

            public TimeSpan ChunkDownloadDelay { get; set; }

            public int MaxActiveChunkDownloads => Volatile.Read(ref _maxActiveChunkDownloads);

            public int? InterruptedChunkNumber { get; set; }

            public int InterruptedChunkFailuresRemaining { get; set; }

            public int? CorruptedChunkNumber { get; set; }

            public int CorruptedChunkResponsesRemaining { get; set; }

            public int UpdateContentFailuresRemaining { get; set; }

            public Task<NodeFileManifestDto> CreateFromChunksAsync(
                CreateFileFromChunksRequestDto request,
                CancellationToken cancellationToken = default)
            {
                CreateRequests.Add(request);
                NodeFileManifestDto created = FileFromRequest(Guid.NewGuid(), request);
                Files[created.Id] = created;
                return Task.FromResult(created);
            }

            public Task<NodeFileManifestDto> UpdateContentAsync(
                Guid nodeFileId,
                CreateFileFromChunksRequestDto request,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                UpdateRequests.Add((nodeFileId, request, expectedETag));
                if (UpdateContentFailuresRemaining > 0)
                {
                    UpdateContentFailuresRemaining--;
                    throw new CottonApiException(
                        HttpStatusCode.ServiceUnavailable,
                        "{\"message\":\"File update interrupted.\"}",
                        "Cotton API file update failed with status 503 (ServiceUnavailable).");
                }

                NodeFileManifestDto updated = FileFromRequest(nodeFileId, request);
                Files[nodeFileId] = updated;
                return Task.FromResult(updated);
            }

            public Task<NodeFileManifestDto> MoveAsync(
                Guid nodeFileId,
                Guid parentId,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                MoveRequests.Add((nodeFileId, parentId, expectedETag));
                NodeFileManifestDto moved = CloneFile(Files[nodeFileId]);
                moved.NodeId = parentId;
                moved.ETag = "sha256-moved-" + MoveRequests.Count;
                Files[nodeFileId] = moved;
                return Task.FromResult(moved);
            }

            public Task<NodeFileManifestDto> RenameAsync(
                Guid nodeFileId,
                string name,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                RenameRequests.Add((nodeFileId, name, expectedETag));
                NodeFileManifestDto renamed = CloneFile(Files[nodeFileId]);
                renamed.Name = name;
                renamed.ETag = "sha256-renamed-" + RenameRequests.Count;
                Files[nodeFileId] = renamed;
                return Task.FromResult(renamed);
            }

            public Task<NodeFileManifestDto> UpdateMetadataAsync(
                Guid nodeFileId,
                IReadOnlyDictionary<string, string> metadata,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                Deletes.Add((nodeFileId, skipTrash, expectedETag));
                return Task.CompletedTask;
            }

            public Task<RestoreOutcomeDto> RestoreAsync(
                Guid nodeFileId,
                RestoreItemRequestDto? request = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<List<FileVersionDto>> GetVersionsAsync(Guid nodeFileId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public async Task DownloadContentAsync(
                Guid nodeFileId,
                Stream destination,
                bool download = false,
                IProgress<long>? progress = null,
                CancellationToken cancellationToken = default)
            {
                byte[] bytes = Downloads[nodeFileId];
                await destination.WriteAsync(bytes, cancellationToken);
                progress?.Report(bytes.Length);
            }

            public async Task DownloadContentRangeAsync(
                Guid nodeFileId,
                Stream destination,
                long offset,
                long length,
                string? expectedETag = null,
                IProgress<long>? progress = null,
                CancellationToken cancellationToken = default)
            {
                RangeDownloads.Add((nodeFileId, offset, length, expectedETag));
                byte[] bytes = Downloads[nodeFileId];
                await destination.WriteAsync(
                    bytes.AsMemory(checked((int)offset), checked((int)length)),
                    cancellationToken);
                progress?.Report(length);
            }

            public async Task<int> DownloadContentChunkAsync(
                Guid nodeFileId,
                int chunkNumber,
                Stream destination,
                string? expectedETag = null,
                IProgress<long>? progress = null,
                CancellationToken cancellationToken = default)
            {
                int active = Interlocked.Increment(ref _activeChunkDownloads);
                try
                {
                    int previous;
                    do
                    {
                        previous = Volatile.Read(ref _maxActiveChunkDownloads);
                    }
                    while (active > previous
                        && Interlocked.CompareExchange(ref _maxActiveChunkDownloads, active, previous) != previous);

                    lock (ChunkDownloads)
                    {
                        ChunkDownloads.Add((nodeFileId, chunkNumber, expectedETag));
                    }

                    if (ChunkDownloadDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(ChunkDownloadDelay, cancellationToken);
                    }

                    byte[] bytes = Downloads[nodeFileId];
                    int count = (int)((bytes.Length + (long)DownloadChunkSizeBytes - 1) / DownloadChunkSizeBytes);
                    int offset = checked(chunkNumber * DownloadChunkSizeBytes);
                    int length = Math.Min(DownloadChunkSizeBytes, bytes.Length - offset);
                    if (InterruptedChunkNumber == chunkNumber && InterruptedChunkFailuresRemaining > 0)
                    {
                        InterruptedChunkFailuresRemaining--;
                        await destination.WriteAsync(bytes.AsMemory(offset, Math.Max(1, length / 2)), cancellationToken);
                        throw new HttpIOException(HttpRequestError.ResponseEnded, "Chunk response ended early.");
                    }

                    if (CorruptedChunkNumber == chunkNumber && CorruptedChunkResponsesRemaining > 0)
                    {
                        CorruptedChunkResponsesRemaining--;
                        byte[] corrupted = bytes.AsSpan(offset, length).ToArray();
                        corrupted[0] ^= 0xff;
                        await destination.WriteAsync(corrupted, cancellationToken);
                        return count;
                    }

                    await destination.WriteAsync(bytes.AsMemory(offset, length), cancellationToken);
                    progress?.Report(length);
                    return count;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeChunkDownloads);
                }
            }

            private static NodeFileManifestDto FileFromRequest(Guid id, CreateFileFromChunksRequestDto request)
            {
                return new NodeFileManifestDto
                {
                    Id = id,
                    NodeId = request.NodeId,
                    FileManifestId = Guid.NewGuid(),
                    OriginalNodeFileId = request.OriginalNodeFileId ?? id,
                    OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Name = request.Name,
                    ContentType = request.ContentType,
                    ContentHash = request.Hash,
                    ETag = "sha256-" + request.Hash,
                };
            }

            private static NodeFileManifestDto CloneFile(NodeFileManifestDto source)
            {
                return new NodeFileManifestDto
                {
                    Id = source.Id,
                    NodeId = source.NodeId,
                    FileManifestId = source.FileManifestId,
                    OriginalNodeFileId = source.OriginalNodeFileId,
                    OwnerId = source.OwnerId,
                    Name = source.Name,
                    ContentType = source.ContentType,
                    SizeBytes = source.SizeBytes,
                    ContentHash = source.ContentHash,
                    ETag = source.ETag,
                    CreatedAt = source.CreatedAt,
                    UpdatedAt = source.UpdatedAt,
                    Metadata = source.Metadata is null
                        ? []
                        : new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
                };
            }
        }

    }
}
