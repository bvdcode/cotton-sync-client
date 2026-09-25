// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal class DownloadChunkCache : IAsyncDisposable
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        private static readonly Dictionary<string, DownloadChunkCache> ActiveCaches = new(PathComparer);
        private static readonly Dictionary<string, DateTime> PrunedRoots = new(PathComparer);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
        private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly string _directory;
        private int _references;

        private DownloadChunkCache(string directory)
        {
            _directory = directory;
        }

        public static async Task<DownloadChunkCache> AcquireAsync(
            string rootDirectory,
            Guid nodeFileId,
            string expectedETag,
            CancellationToken cancellationToken)
        {
            if (!expectedETag.StartsWith("sha256-", StringComparison.Ordinal)
                || expectedETag.Length != 71
                || !expectedETag[7..].All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Remote file has an invalid content ETag.");
            }

            string fullRoot = Path.GetFullPath(rootDirectory);
            string directory = Path.Combine(
                fullRoot,
                nodeFileId.ToString("N") + "-" + expectedETag[7..].ToLowerInvariant());
            DownloadChunkCache cache;
            lock (ActiveCaches)
            {
                DateTime now = DateTime.UtcNow;
                if (!PrunedRoots.TryGetValue(fullRoot, out DateTime lastPruned)
                    || now - lastPruned >= PruneInterval)
                {
                    PruneExpired(fullRoot, now - Retention);
                    PrunedRoots[fullRoot] = now;
                }

                if (!ActiveCaches.TryGetValue(directory, out cache!))
                {
                    cache = new DownloadChunkCache(directory);
                    ActiveCaches.Add(directory, cache);
                }

                cache._references++;
            }

            try
            {
                await cache._semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return cache;
            }
            catch
            {
                cache.ReleaseReference();
                throw;
            }
        }

        public async Task<string> GetChunkAsync(
            int chunkNumber,
            Func<Stream, CancellationToken, Task> download,
            bool refresh,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_directory);
            string chunkPath = Path.Combine(_directory, chunkNumber.ToString("D8") + ".chunk");
            if (File.Exists(chunkPath))
            {
                if (!refresh && new FileInfo(chunkPath).Length > 0)
                {
                    return chunkPath;
                }

                File.Delete(chunkPath);
            }

            string temporaryPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await using (FileStream stream = new(
                    temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await download(stream, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (stream.Length == 0)
                    {
                        throw new InvalidDataException("Downloaded content chunk is empty.");
                    }
                }

                File.Move(temporaryPath, chunkPath, overwrite: true);
                return chunkPath;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public void Complete()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        public ValueTask DisposeAsync()
        {
            _semaphore.Release();
            ReleaseReference();
            return ValueTask.CompletedTask;
        }

        private void ReleaseReference()
        {
            lock (ActiveCaches)
            {
                _references--;
                if (_references == 0)
                {
                    ActiveCaches.Remove(_directory);
                    _semaphore.Dispose();
                }
            }
        }

        private static void PruneExpired(string rootDirectory, DateTime cutoff)
        {
            Directory.CreateDirectory(rootDirectory);
            foreach (string directory in Directory.EnumerateDirectories(rootDirectory))
            {
                string name = Path.GetFileName(directory);
                if (name.Length != 97
                    || name[32] != '-'
                    || !Guid.TryParseExact(name[..32], "N", out _)
                    || !name[33..].All(Uri.IsHexDigit)
                    || ActiveCaches.ContainsKey(directory)
                    || Directory.GetLastWriteTimeUtc(directory) >= cutoff)
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
