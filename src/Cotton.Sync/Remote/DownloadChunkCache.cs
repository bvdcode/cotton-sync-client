// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using Cotton.Files;

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
            FileContentManifestDto manifest,
            CancellationToken cancellationToken)
        {
            string fullRoot = Path.GetFullPath(rootDirectory);
            string directory = Path.Combine(
                fullRoot,
                manifest.FileManifestId.ToString("N") + "-" + manifest.ContentHash);
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

        public async Task<string> GetVerifiedChunkAsync(
            FileContentManifestChunkDto chunk,
            Func<Stream, CancellationToken, Task> download,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_directory);
            string chunkPath = Path.Combine(_directory, chunk.Index.ToString("D8") + ".chunk");
            if (File.Exists(chunkPath))
            {
                if (await IsValidAsync(chunkPath, chunk, cancellationToken).ConfigureAwait(false))
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
                    if (stream.Length != chunk.Length)
                    {
                        throw new InvalidDataException("Downloaded chunk length does not match the content manifest.");
                    }

                    stream.Position = 0;
                    string actualHash = Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(actualHash, chunk.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Downloaded chunk hash does not match the content manifest.");
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

        private static async Task<bool> IsValidAsync(
            string path,
            FileContentManifestChunkDto chunk,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != chunk.Length)
            {
                return false;
            }

            string actualHash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return string.Equals(actualHash, chunk.Hash, StringComparison.OrdinalIgnoreCase);
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
