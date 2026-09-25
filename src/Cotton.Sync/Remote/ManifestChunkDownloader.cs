// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using Cotton.Files;
using Cotton.Sdk;
using Cotton.Sdk.Files;

namespace Cotton.Sync.Remote
{
    internal class ManifestChunkDownloader(ICottonFileClient _files, SdkRemoteFileSynchronizerOptions _options)
    {
        private const int CopyBufferSize = 1024 * 128;
        private const int MaximumDownloadRangeBytes = 4 * 1024 * 1024;

        public async Task DownloadAsync(
            Guid nodeFileId,
            Stream destination,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            FileContentManifestDto manifest = await _files
                .GetContentManifestAsync(nodeFileId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ValidateManifest(nodeFileId, manifest);
            await using DownloadChunkCache cache = await DownloadChunkCache
                .AcquireAsync(_options.DownloadCacheDirectory, manifest, cancellationToken)
                .ConfigureAwait(false);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long completed = 0;
            foreach (FileContentManifestChunkDto chunk in manifest.Chunks)
            {
                string chunkPath = await GetChunkWithRetryAsync(cache, manifest, chunk, completed, progress, cancellationToken)
                    .ConfigureAwait(false);
                await CopyChunkAsync(chunkPath, destination, 0, chunk.Length, hash, cancellationToken)
                    .ConfigureAwait(false);
                completed += chunk.Length;
                progress?.Report(completed);
            }

            string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded file hash does not match the content manifest.");
            }

            cache.Complete();
        }

        public async Task DownloadRangeAsync(
            Guid nodeFileId,
            Stream destination,
            long offset,
            long length,
            string? expectedETag,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            FileContentManifestDto manifest = await _files
                .GetContentManifestAsync(nodeFileId, expectedETag, cancellationToken)
                .ConfigureAwait(false);
            ValidateManifest(nodeFileId, manifest);
            if (offset > manifest.SizeBytes || length > manifest.SizeBytes - offset)
            {
                throw new InvalidDataException("Requested range exceeds the content manifest.");
            }

            await using DownloadChunkCache cache = await DownloadChunkCache
                .AcquireAsync(_options.DownloadCacheDirectory, manifest, cancellationToken)
                .ConfigureAwait(false);
            long completed = 0;
            long end = offset + length;
            foreach (FileContentManifestChunkDto chunk in manifest.Chunks)
            {
                long chunkEnd = chunk.Offset + chunk.Length;
                if (chunkEnd <= offset || chunk.Offset >= end)
                {
                    continue;
                }

                string chunkPath = await GetChunkWithRetryAsync(cache, manifest, chunk, 0, null, cancellationToken)
                    .ConfigureAwait(false);
                long copyStart = Math.Max(offset, chunk.Offset);
                long copyEnd = Math.Min(end, chunkEnd);
                await CopyChunkAsync(chunkPath, destination, copyStart - chunk.Offset, copyEnd - copyStart, null, cancellationToken)
                    .ConfigureAwait(false);
                completed += copyEnd - copyStart;
                progress?.Report(completed);
            }

            cache.Complete();
        }

        private async Task<string> GetChunkWithRetryAsync(
            DownloadChunkCache cache,
            FileContentManifestDto manifest,
            FileContentManifestChunkDto chunk,
            long completed,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await cache.GetVerifiedChunkAsync(
                        chunk,
                        (stream, token) => DownloadChunkRangesAsync(
                            manifest, chunk, stream, completed, progress, token),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception) when (
                    !cancellationToken.IsCancellationRequested && attempt < _options.MaxDownloadChunkAttempts)
                {
                    Trace.TraceWarning(
                        "Chunk {0} failed integrity validation; retrying attempt {1}: {2}",
                        chunk.Index, attempt + 1, exception.Message);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task DownloadChunkRangesAsync(
            FileContentManifestDto manifest,
            FileContentManifestChunkDto chunk,
            Stream destination,
            long completed,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            long transferred = 0;
            while (transferred < chunk.Length)
            {
                long rangeLength = Math.Min(MaximumDownloadRangeBytes, chunk.Length - transferred);
                for (int attempt = 1; ; attempt++)
                {
                    destination.SetLength(transferred);
                    destination.Position = transferred;
                    try
                    {
                        await _files.DownloadContentRangeAsync(
                            manifest.NodeFileId,
                            destination,
                            chunk.Offset + transferred,
                            rangeLength,
                            manifest.ETag,
                            progress is null ? null : new ChunkProgress(progress, completed + transferred, rangeLength),
                            cancellationToken).ConfigureAwait(false);
                        if (destination.Length != transferred + rangeLength)
                        {
                            throw new EndOfStreamException("Downloaded range ended before its expected length.");
                        }

                        break;
                    }
                    catch (Exception exception) when (
                        !cancellationToken.IsCancellationRequested
                        && attempt < _options.MaxDownloadChunkAttempts
                        && IsRetryable(exception))
                    {
                        Trace.TraceWarning(
                            "Download range at offset {0} failed; retrying attempt {1}: {2}",
                            chunk.Offset + transferred, attempt + 1, exception.Message);
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                    }
                }

                transferred += rangeLength;
            }
        }

        private static async Task CopyChunkAsync(
            string path,
            Stream destination,
            long offset,
            long length,
            IncrementalHash? hash,
            CancellationToken cancellationToken)
        {
            await using FileStream source = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            source.Position = offset;
            byte[] buffer = new byte[CopyBufferSize];
            long remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, buffer.Length);
                int read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("Verified chunk ended before its expected length.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash?.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
            }
        }

        private static void ValidateManifest(Guid nodeFileId, FileContentManifestDto manifest)
        {
            if (manifest is null
                || manifest.NodeFileId != nodeFileId
                || manifest.FileManifestId == Guid.Empty
                || manifest.SizeBytes < 0
                || string.IsNullOrWhiteSpace(manifest.ETag)
                || !IsSha256(manifest.ContentHash)
                || manifest.Chunks is null)
            {
                throw new InvalidDataException("Server returned an invalid file content manifest.");
            }

            long offset = 0;
            for (int index = 0; index < manifest.Chunks.Count; index++)
            {
                FileContentManifestChunkDto chunk = manifest.Chunks[index];
                if (chunk is null || chunk.Index != index || chunk.Offset != offset || chunk.Length <= 0 || !IsSha256(chunk.Hash))
                {
                    throw new InvalidDataException("Server returned invalid file chunk metadata.");
                }

                offset = checked(offset + chunk.Length);
            }

            if (offset != manifest.SizeBytes)
            {
                throw new InvalidDataException("File chunks do not cover the complete content manifest.");
            }
        }

        private static bool IsSha256(string? value)
        {
            return value is { Length: 64 } && value.All(Uri.IsHexDigit);
        }

        private static bool IsRetryable(Exception exception)
        {
            return exception switch
            {
                InvalidDataException => true,
                EndOfStreamException => true,
                HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => true,
                CottonApiException apiException => apiException.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.Locked
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout,
                HttpRequestException requestException => requestException.StatusCode is null
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.Locked
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout,
                TimeoutException => true,
                TaskCanceledException => true,
                _ => false,
            };
        }

    }
}
