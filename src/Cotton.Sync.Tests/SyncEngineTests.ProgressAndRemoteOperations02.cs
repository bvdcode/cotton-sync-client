// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {

        private class FakeRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            private const string UploadedContentType = "application/octet-stream";
            private const string UploadedEtagPrefix = "sha256-";
            private static readonly Guid UploadedOwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            private static readonly DateTime UploadedAt = new(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc);

            public List<UploadCall> Uploads { get; } = [];

            public List<MoveCall> Moves { get; } = [];

            public List<string> UploadInputContentHashes { get; } = [];

            public List<Guid> DownloadCalls { get; } = [];

            public List<(Guid NodeFileId, bool SkipTrash, string? ExpectedETag)> Deletes { get; } = [];

            public Dictionary<Guid, byte[]> Downloads { get; } = [];

            public HashSet<Guid> UploadFailureIds { get; } = [];

            public HashSet<string> UploadFailureRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> CreateConflictRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<Guid> DownloadFailureIds { get; } = [];

            public HashSet<Guid> PartialDownloadFailureIds { get; } = [];

            public HashSet<Guid> DeleteFailureIds { get; } = [];

            public HashSet<Guid> PreconditionFailedUploadIds { get; } = [];

            public HashSet<Guid> PreconditionFailedDeleteIds { get; } = [];

            public HashSet<Guid> PreconditionFailedMoveIds { get; } = [];

            public HashSet<string> LocalUnavailableUploadRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public string? EmptyLocalHashUploadContentHash { get; set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                ThrowIfUploadShouldFail(relativePath, localFile, existingRemoteFile);
                UploadInputContentHashes.Add(localFile.ContentHash);
                string uploadedContentHash = ResolveUploadedContentHash(localFile);
                NodeFileManifestDto uploaded = CreateUploadedManifest(
                    rootNodeId,
                    relativePath,
                    localFile,
                    existingRemoteFile,
                    uploadedContentHash);
                Uploads.Add(new UploadCall(rootNodeId, relativePath, localFile, existingRemoteFile, uploaded));
                return Task.FromResult(uploaded);
            }

            private void ThrowIfUploadShouldFail(
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile)
            {
                if (existingRemoteFile is null && CreateConflictRelativePaths.Contains(relativePath))
                {
                    throw new HttpRequestException(
                        "Remote file already exists.",
                        inner: null,
                        HttpStatusCode.Conflict);
                }

                if (existingRemoteFile is not null && PreconditionFailedUploadIds.Contains(existingRemoteFile.Id))
                {
                    throw new HttpRequestException(
                        "Remote file changed before upload.",
                        inner: null,
                        HttpStatusCode.PreconditionFailed);
                }

                if (existingRemoteFile is not null && UploadFailureIds.Contains(existingRemoteFile.Id))
                {
                    throw new InvalidOperationException("Remote upload failed.");
                }

                if (UploadFailureRelativePaths.Contains(relativePath))
                {
                    throw new HttpRequestException(
                        "Remote upload failed.",
                        inner: null,
                        HttpStatusCode.ServiceUnavailable);
                }

                if (LocalUnavailableUploadRelativePaths.Contains(relativePath))
                {
                    throw new LocalFileUnavailableException(
                        relativePath,
                        localFile.FullPath,
                        "the file changed during upload.");
                }
            }

            private string ResolveUploadedContentHash(LocalFileSnapshot localFile)
            {
                return string.IsNullOrWhiteSpace(localFile.ContentHash)
                    ? EmptyLocalHashUploadContentHash ?? localFile.ContentHash
                    : localFile.ContentHash;
            }

            private static NodeFileManifestDto CreateUploadedManifest(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile,
                string uploadedContentHash)
            {
                return new NodeFileManifestDto
                {
                    Id = existingRemoteFile?.Id ?? Guid.NewGuid(),
                    NodeId = existingRemoteFile?.NodeId ?? rootNodeId,
                    FileManifestId = Guid.NewGuid(),
                    OriginalNodeFileId = existingRemoteFile?.OriginalNodeFileId == Guid.Empty
                        ? Guid.NewGuid()
                        : existingRemoteFile?.OriginalNodeFileId ?? Guid.NewGuid(),
                    OwnerId = UploadedOwnerId,
                    Name = relativePath.Split('/')[^1],
                    ContentType = UploadedContentType,
                    SizeBytes = localFile.SizeBytes,
                    ContentHash = uploadedContentHash,
                    ETag = UploadedEtagPrefix + uploadedContentHash,
                    CreatedAt = UploadedAt,
                    UpdatedAt = UploadedAt,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath.Replace('\\', '/') },
                };
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                if (PreconditionFailedMoveIds.Contains(existingRemoteFile.Id))
                {
                    throw new HttpRequestException(
                        "Remote file changed before move.",
                        inner: null,
                        HttpStatusCode.PreconditionFailed);
                }

                string normalizedPath = relativePath.Replace('\\', '/');
                NodeFileManifestDto moved = new()
                {
                    Id = existingRemoteFile.Id,
                    NodeId = rootNodeId,
                    FileManifestId = existingRemoteFile.FileManifestId,
                    OriginalNodeFileId = existingRemoteFile.OriginalNodeFileId,
                    OwnerId = existingRemoteFile.OwnerId,
                    Name = normalizedPath.Split('/')[^1],
                    ContentType = existingRemoteFile.ContentType,
                    SizeBytes = existingRemoteFile.SizeBytes,
                    ContentHash = existingRemoteFile.ContentHash,
                    ETag = existingRemoteFile.ETag,
                    CreatedAt = existingRemoteFile.CreatedAt,
                    UpdatedAt = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string> { ["relativePath"] = normalizedPath },
                };
                Moves.Add(new MoveCall(rootNodeId, normalizedPath, existingRemoteFile, moved));
                return Task.FromResult(moved);
            }

            public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                DownloadCalls.Add(nodeFileId);
                if (DownloadFailureIds.Contains(nodeFileId))
                {
                    throw new InvalidOperationException("Remote download failed.");
                }

                byte[] bytes = Downloads[nodeFileId];
                if (PartialDownloadFailureIds.Contains(nodeFileId))
                {
                    int partialLength = Math.Max(1, bytes.Length / 2);
                    destination.Write(bytes, 0, partialLength);
                    throw new CottonApiException(
                        HttpStatusCode.ServiceUnavailable,
                        "{\"message\":\"Download interrupted.\"}",
                        "Cotton API download failed with status 503 (ServiceUnavailable).");
                }

                return destination.WriteAsync(bytes, cancellationToken).AsTask();
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                Deletes.Add((nodeFileId, skipTrash, expectedETag));
                if (DeleteFailureIds.Contains(nodeFileId))
                {
                    throw new InvalidOperationException("Remote delete failed.");
                }

                if (PreconditionFailedDeleteIds.Contains(nodeFileId))
                {
                    throw new HttpRequestException(
                        "Remote file changed before delete.",
                        inner: null,
                        HttpStatusCode.PreconditionFailed);
                }

                return Task.CompletedTask;
            }
        }


        private class FakeRemoteDirectorySynchronizer : IRemoteDirectorySynchronizer
        {
            public List<CreateDirectoryCall> CreateAttempts { get; } = [];

            public List<CreateDirectoryCall> Creates { get; } = [];

            public List<(Guid NodeId, bool SkipTrash)> Deletes { get; } = [];

            public List<(Guid ParentNodeId, string Name)> ConflictCreates { get; } = [];

            public List<NodeDto> ExistingDirectories { get; } = [];

            public List<(Guid ParentNodeId, string Name)> FindChildDirectoryCalls { get; } = [];

            public Task<NodeDto?> FindChildDirectoryAsync(
                Guid parentNodeId,
                string name,
                CancellationToken cancellationToken = default)
            {
                FindChildDirectoryCalls.Add((parentNodeId, name));
                NodeDto? match = ExistingDirectories.FirstOrDefault(node =>
                    node.ParentId == parentNodeId
                    && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(match);
            }

            public Task<NodeDto> CreateDirectoryAsync(
                Guid parentNodeId,
                string name,
                CancellationToken cancellationToken = default)
            {
                CreateAttempts.Add(new CreateDirectoryCall(parentNodeId, name, new NodeDto()));
                if (ConflictCreates.Any(item =>
                    item.ParentNodeId == parentNodeId
                    && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new CottonApiException(
                        HttpStatusCode.Conflict,
                        "{\"message\":\"A folder with the same name already exists.\"}",
                        "Cotton API request PUT /api/v1/layouts/nodes failed with status 409 (Conflict).");
                }

                NodeDto node = new()
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentNodeId,
                    Name = name,
                };
                Creates.Add(new CreateDirectoryCall(parentNodeId, name, node));
                return Task.FromResult(node);
            }

            public Task DeleteDirectoryAsync(Guid nodeId, bool skipTrash = false, CancellationToken cancellationToken = default)
            {
                Deletes.Add((nodeId, skipTrash));
                return Task.CompletedTask;
            }
        }
    }
}
