// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Buffers;
using System.Security.Cryptography;
using Cotton.Sdk.Chunks;

namespace Cotton.Sync.Remote
{
    internal class RemoteChunkUploader
    {
        private const string ContentType = "application/octet-stream";
        private const int MaximumInitialChunkCollectionCapacity = 65_536;
        private readonly ICottonChunkClient _chunks;
        private readonly int _maxConcurrentUploads;

        public RemoteChunkUploader(ICottonChunkClient chunks, int maxConcurrentUploads)
        {
            _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentUploads);
            _maxConcurrentUploads = maxConcurrentUploads;
        }

        public async Task<UploadedChunks> UploadAsync(
            string relativePath,
            string filePath,
            long totalBytes,
            int chunkSize,
            IProgress<SyncTransferProgress>? transferProgress,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            int capacity = EstimateChunkCollectionCapacity(totalBytes, chunkSize);
            List<string> chunkHashes = new(capacity);
            HashSet<string> knownChunkHashes = new(capacity, StringComparer.OrdinalIgnoreCase);
            List<Task<int>> pendingUploads = new(_maxConcurrentUploads);
            long transferredBytes = 0;
            using IncrementalHash contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                await using FileStream stream = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: Math.Min(chunkSize, 1024 * 128),
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    int read = await ReadChunkAsync(stream, buffer, chunkSize, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    string hash = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, read)));
                    contentHash.AppendData(buffer, 0, read);
                    chunkHashes.Add(hash);
                    pendingUploads.Add(CreateUploadTask(hash, buffer, read, knownChunkHashes, cancellationToken));
                    if (pendingUploads.Count >= _maxConcurrentUploads)
                    {
                        transferredBytes = await FlushAsync(
                            pendingUploads,
                            transferredBytes,
                            relativePath,
                            totalBytes,
                            transferProgress).ConfigureAwait(false);
                    }
                }

                transferredBytes = await FlushAsync(
                    pendingUploads,
                    transferredBytes,
                    relativePath,
                    totalBytes,
                    transferProgress).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (chunkHashes.Count == 0)
            {
                string emptyHash = Convert.ToHexStringLower(SHA256.HashData(ReadOnlySpan<byte>.Empty));
                await UploadIfMissingAsync(emptyHash, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                ReportTransfer(transferProgress, relativePath, transferredBytes: 0, totalBytes);
                chunkHashes.Add(emptyHash);
            }

            string fullContentHash = Convert.ToHexStringLower(contentHash.GetHashAndReset());
            return new UploadedChunks(chunkHashes, fullContentHash);
        }

        private Task<int> CreateUploadTask(
            string hash,
            byte[] sourceBuffer,
            int count,
            HashSet<string> knownChunkHashes,
            CancellationToken cancellationToken)
        {
            if (!knownChunkHashes.Add(hash))
            {
                return Task.FromResult(count);
            }

            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(count);
            sourceBuffer.AsSpan(0, count).CopyTo(chunkBuffer);
            return UploadIfMissingAsync(hash, chunkBuffer, count, cancellationToken);
        }

        private async Task UploadIfMissingAsync(
            string hash,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            if (await _chunks.ExistsAsync(hash, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await using MemoryStream stream = new MemoryStream(content.ToArray(), writable: false);
            await _chunks.UploadRawAsync(hash, stream, ContentType, cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> UploadIfMissingAsync(
            string hash,
            byte[] buffer,
            int count,
            CancellationToken cancellationToken)
        {
            try
            {
                if (await _chunks.ExistsAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    return count;
                }

                await using MemoryStream stream = new MemoryStream(buffer, 0, count, writable: false);
                await _chunks.UploadRawAsync(hash, stream, ContentType, cancellationToken).ConfigureAwait(false);
                return count;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task<long> FlushAsync(
            List<Task<int>> pendingUploads,
            long transferredBytes,
            string relativePath,
            long totalBytes,
            IProgress<SyncTransferProgress>? transferProgress)
        {
            while (pendingUploads.Count > 0)
            {
                Task<int> completedTask = await Task.WhenAny(pendingUploads).ConfigureAwait(false);
                pendingUploads.Remove(completedTask);
                int bytes;
                try
                {
                    bytes = await completedTask.ConfigureAwait(false);
                }
                catch
                {
                    ObserveFailures(pendingUploads);
                    throw;
                }

                transferredBytes += bytes;
                ReportTransfer(transferProgress, relativePath, transferredBytes, totalBytes);
            }

            return transferredBytes;
        }

        private static void ObserveFailures(List<Task<int>> pendingUploads)
        {
            if (pendingUploads.Count == 0)
            {
                return;
            }

            Task pendingBatch = Task.WhenAll(pendingUploads);
            pendingUploads.Clear();
            _ = pendingBatch.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private static int EstimateChunkCollectionCapacity(long totalBytes, int chunkSize)
        {
            if (totalBytes <= 0)
            {
                return 0;
            }

            long estimatedChunkCount = ((totalBytes - 1) / chunkSize) + 1;
            return estimatedChunkCount > MaximumInitialChunkCollectionCapacity
                ? MaximumInitialChunkCollectionCapacity
                : (int)estimatedChunkCount;
        }

        private static void ReportTransfer(
            IProgress<SyncTransferProgress>? progress,
            string relativePath,
            long transferredBytes,
            long? totalBytes)
        {
            progress?.Report(new SyncTransferProgress(
                SyncTransferDirection.Upload,
                relativePath,
                transferredBytes,
                totalBytes));
        }

        private static async Task<int> ReadChunkAsync(
            FileStream stream,
            byte[] buffer,
            int chunkSize,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < chunkSize)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, chunkSize - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }
    }
}
