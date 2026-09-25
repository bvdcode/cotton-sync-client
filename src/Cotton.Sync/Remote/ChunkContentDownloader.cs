// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using Cotton.Sdk;
using Cotton.Sdk.Files;

namespace Cotton.Sync.Remote
{
    internal class ChunkContentDownloader(ICottonFileClient _files, SdkRemoteFileSynchronizerOptions _options)
    {
        private const int CopyBufferSize = 1024 * 128;

        public async Task DownloadAsync(
            RemoteFileDownloadIdentity file,
            Stream destination,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentException.ThrowIfNullOrWhiteSpace(file.ETag);
            if (file.SizeBytes is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(file));
            }

            if (file.SizeBytes == 0)
            {
                return;
            }

            await using DownloadChunkCache cache = await DownloadChunkCache
                .AcquireAsync(_options.DownloadCacheDirectory, file.NodeFileId, file.ETag, cancellationToken)
                .ConfigureAwait(false);

            int chunkCount = 0;
            string firstPath = await GetChunkWithRetryAsync(
                cache,
                0,
                async (stream, token) =>
                {
                    chunkCount = await _files.DownloadContentChunkAsync(
                        file.NodeFileId, 0, stream, file.ETag, cancellationToken: token).ConfigureAwait(false);
                },
                refresh: true,
                cancellationToken).ConfigureAwait(false);
            if (chunkCount <= 0 || (file.SizeBytes.HasValue && chunkCount > file.SizeBytes.Value))
            {
                throw new InvalidDataException("Server returned an invalid content chunk count.");
            }

            string[] paths = new string[chunkCount];
            paths[0] = firstPath;
            long completed = new FileInfo(firstPath).Length;
            progress?.Report(completed);
            await Parallel.ForEachAsync(
                Enumerable.Range(1, chunkCount - 1),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaxConcurrentChunkDownloads,
                    CancellationToken = cancellationToken,
                },
                async (chunkNumber, token) =>
                {
                    string path = await GetChunkWithRetryAsync(
                        cache,
                        chunkNumber,
                        async (stream, downloadToken) =>
                        {
                            int receivedCount = await _files.DownloadContentChunkAsync(
                                file.NodeFileId,
                                chunkNumber,
                                stream,
                                file.ETag,
                                cancellationToken: downloadToken).ConfigureAwait(false);
                            if (receivedCount != chunkCount)
                            {
                                throw new InvalidDataException("Content chunk count changed during download.");
                            }
                        },
                        refresh: false,
                        token).ConfigureAwait(false);
                    paths[chunkNumber] = path;
                    long transferred = Interlocked.Add(ref completed, new FileInfo(path).Length);
                    progress?.Report(transferred);
                }).ConfigureAwait(false);

            if (file.SizeBytes.HasValue && completed != file.SizeBytes.Value)
            {
                cache.Complete();
                throw new InvalidDataException(
                    $"Downloaded content chunks total {completed} bytes; expected {file.SizeBytes.Value} bytes.");
            }

            foreach (string path in paths)
            {
                await using FileStream source = new(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            }

            cache.Complete();
        }

        private async Task<string> GetChunkWithRetryAsync(
            DownloadChunkCache cache,
            int chunkNumber,
            Func<Stream, CancellationToken, Task> download,
            bool refresh,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await cache.GetChunkAsync(
                        chunkNumber, download, refresh, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested
                    && attempt < _options.MaxDownloadChunkAttempts
                    && IsRetryable(exception))
                {
                    Trace.TraceWarning(
                        "Content chunk {0} failed; retrying attempt {1}: {2}",
                        chunkNumber, attempt + 1, exception.Message);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool IsRetryable(Exception exception)
        {
            return exception switch
            {
                EndOfStreamException => true,
                HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => true,
                CottonApiException apiException => apiException.StatusCode is HttpStatusCode.OK
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout,
                HttpRequestException requestException => requestException.StatusCode is null
                    or HttpStatusCode.RequestTimeout
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
