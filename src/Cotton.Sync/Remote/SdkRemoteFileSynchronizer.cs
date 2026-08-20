// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Synchronizes remote files through Cotton SDK clients.
    /// </summary>
    public class SdkRemoteFileSynchronizer : IRemoteFileTransferProgressSynchronizer, IRemoteFileRangeSynchronizer
    {
        private const string DefaultContentType = "application/octet-stream";
        private readonly ICottonCloudClient _client;
        private readonly RemoteChunkUploader _chunkUploader;
        private readonly SdkRemoteFileSynchronizerOptions _options;
        private readonly RemoteDirectoryPathResolver _directoryResolver;
        private int? _resolvedChunkSizeBytes;

        /// <summary>
        /// Initializes a new instance of the <see cref="SdkRemoteFileSynchronizer" /> class.
        /// </summary>
        public SdkRemoteFileSynchronizer(ICottonCloudClient client, SdkRemoteFileSynchronizerOptions? options = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options ?? new SdkRemoteFileSynchronizerOptions();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.DirectoryPageSize);
            if (_options.ChunkSizeBytes.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.ChunkSizeBytes.Value);
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConcurrentChunkUploads);
            _directoryResolver = new RemoteDirectoryPathResolver(_client.Nodes, _options.DirectoryPageSize);
            _chunkUploader = new RemoteChunkUploader(_client.Chunks, _options.MaxConcurrentChunkUploads);
        }

        /// <inheritdoc />
        public async Task<NodeFileManifestDto> UploadFileAsync(
            Guid rootNodeId,
            string relativePath,
            LocalFileSnapshot localFile,
            NodeFileManifestDto? existingRemoteFile = null,
            CancellationToken cancellationToken = default)
        {
            return await UploadFileAsync(
                rootNodeId,
                relativePath,
                localFile,
                existingRemoteFile,
                transferProgress: null,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<NodeFileManifestDto> UploadFileAsync(
            Guid rootNodeId,
            string relativePath,
            LocalFileSnapshot localFile,
            NodeFileManifestDto? existingRemoteFile,
            IProgress<SyncTransferProgress>? transferProgress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(localFile);
            string normalizedPath = SyncPath.Normalize(relativePath);
            Guid parentNodeId = await _directoryResolver
                .EnsureParentAsync(rootNodeId, normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            ReportTransfer(
                transferProgress,
                SyncTransferDirection.Upload,
                normalizedPath,
                transferredBytes: 0,
                totalBytes: localFile.SizeBytes);
            int chunkSize = await GetChunkSizeAsync(cancellationToken).ConfigureAwait(false);
            UploadedChunks uploadedChunks = await _chunkUploader.UploadAsync(
                normalizedPath,
                localFile.FullPath,
                localFile.SizeBytes,
                chunkSize,
                transferProgress,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(localFile.ContentHash)
                && !string.Equals(localFile.ContentHash, uploadedChunks.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalFileUnavailableException(
                    normalizedPath,
                    localFile.FullPath,
                    "the file changed during upload.");
            }

            CreateFileFromChunksRequestDto request = new CreateFileFromChunksRequestDto
            {
                NodeId = parentNodeId,
                ChunkHashes = uploadedChunks.ChunkHashes,
                Name = Path.GetFileName(normalizedPath),
                ContentType = ResolveContentType(normalizedPath),
                Hash = uploadedChunks.ContentHash,
                OriginalNodeFileId = existingRemoteFile?.OriginalNodeFileId == Guid.Empty ? existingRemoteFile.Id : existingRemoteFile?.OriginalNodeFileId,
            };

            NodeFileManifestDto uploaded = existingRemoteFile is null
                ? await _client.Files.CreateFromChunksAsync(request, cancellationToken).ConfigureAwait(false)
                : await _client.Files.UpdateContentAsync(
                    existingRemoteFile.Id,
                    request,
                    existingRemoteFile.ETag,
                    cancellationToken).ConfigureAwait(false);
            ReportTransfer(
                transferProgress,
                SyncTransferDirection.Upload,
                normalizedPath,
                localFile.SizeBytes,
                localFile.SizeBytes,
                isCompleted: true);
            return uploaded;
        }

        /// <inheritdoc />
        public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            return _client.Files.DownloadContentAsync(nodeFileId, destination, cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public async Task<NodeFileManifestDto> MoveFileAsync(
            Guid rootNodeId,
            string relativePath,
            NodeFileManifestDto existingRemoteFile,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(existingRemoteFile);
            string normalizedPath = SyncPath.Normalize(relativePath);
            Guid parentNodeId = await _directoryResolver
                .EnsureParentAsync(rootNodeId, normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            string targetName = Path.GetFileName(normalizedPath);
            NodeFileManifestDto current = existingRemoteFile;
            if (current.NodeId != parentNodeId)
            {
                current = await _client.Files
                    .MoveAsync(current.Id, parentNodeId, current.ETag, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(current.Name, targetName, StringComparison.Ordinal))
            {
                current = await _client.Files
                    .RenameAsync(current.Id, targetName, current.ETag, cancellationToken)
                    .ConfigureAwait(false);
            }

            return current;
        }

        /// <inheritdoc />
        public async Task DownloadFileAsync(
            Guid nodeFileId,
            string relativePath,
            long? totalBytes,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            string normalizedPath = SyncPath.Normalize(relativePath);
            ReportTransfer(
                transferProgress,
                SyncTransferDirection.Download,
                normalizedPath,
                transferredBytes: 0,
                totalBytes);
            DownloadTransferProgress? progress = transferProgress is null
                ? null
                : new DownloadTransferProgress(transferProgress, normalizedPath, totalBytes);
            await _client.Files
                .DownloadContentAsync(nodeFileId, destination, progress: progress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            long completedBytes = totalBytes ?? progress?.LastTransferredBytes ?? 0;
            ReportTransfer(
                transferProgress,
                SyncTransferDirection.Download,
                normalizedPath,
                completedBytes,
                totalBytes,
                isCompleted: true);
        }

        /// <inheritdoc />
        public async Task DownloadFileRangeAsync(
            Guid nodeFileId,
            string relativePath,
            long offset,
            long length,
            string? expectedETag,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            string normalizedPath = SyncPath.Normalize(relativePath);
            ReportTransfer(
                transferProgress,
                SyncTransferDirection.Download,
                normalizedPath,
                transferredBytes: 0,
                totalBytes: length);
            DownloadTransferProgress? progress = transferProgress is null
                ? null
                : new DownloadTransferProgress(transferProgress, normalizedPath, length);
            await _client.Files
                .DownloadContentRangeAsync(
                    nodeFileId,
                    destination,
                    offset,
                    length,
                    expectedETag,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            ReportTransfer(
                transferProgress,
                SyncTransferDirection.Download,
                normalizedPath,
                length,
                length,
                isCompleted: true);
        }

        /// <inheritdoc />
        public Task DeleteFileAsync(
            Guid nodeFileId,
            bool skipTrash = false,
            string? expectedETag = null,
            CancellationToken cancellationToken = default)
        {
            return _client.Files.DeleteAsync(nodeFileId, skipTrash, expectedETag, cancellationToken);
        }

        private static void ReportTransfer(
            IProgress<SyncTransferProgress>? progress,
            SyncTransferDirection direction,
            string relativePath,
            long transferredBytes,
            long? totalBytes,
            bool isCompleted = false)
        {
            progress?.Report(new SyncTransferProgress(
                direction,
                relativePath,
                transferredBytes,
                totalBytes,
                isCompleted));
        }

        private async Task<int> GetChunkSizeAsync(CancellationToken cancellationToken)
        {
            if (_resolvedChunkSizeBytes.HasValue)
            {
                return _resolvedChunkSizeBytes.Value;
            }

            if (_options.ChunkSizeBytes.HasValue)
            {
                _resolvedChunkSizeBytes = _options.ChunkSizeBytes.Value;
                return _resolvedChunkSizeBytes.Value;
            }

            ClientSettingsDto settings = await _client.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (settings.MaxChunkSizeBytes <= 0)
            {
                throw new InvalidOperationException("Server returned an invalid maximum chunk size.");
            }

            _resolvedChunkSizeBytes = settings.MaxChunkSizeBytes;
            return _resolvedChunkSizeBytes.Value;
        }

        private string ResolveContentType(string relativePath)
        {
            if (_options.ContentTypeResolver is not null)
            {
                return _options.ContentTypeResolver(relativePath);
            }

            string extension = Path.GetExtension(relativePath).ToLowerInvariant();
            return extension switch
            {
                ".css" => "text/css",
                ".csv" => "text/csv",
                ".htm" or ".html" => "text/html",
                ".json" => "application/json",
                ".md" => "text/markdown",
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".txt" => "text/plain",
                ".webp" => "image/webp",
                ".xml" => "application/xml",
                _ => DefaultContentType,
            };
        }

    }
}
