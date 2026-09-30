// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Remote
{
    internal class DownloadCacheLease : IAsyncDisposable
    {
        private const string RootLockName = "downloads.lock";
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
        private readonly FileStream _rootLease;
        private readonly FileStream _fileLease;

        private DownloadCacheLease(FileStream rootLease, FileStream fileLease)
        {
            _rootLease = rootLease;
            _fileLease = fileLease;
        }

        public static async Task<DownloadCacheLease> AcquireAsync(
            string rootDirectory,
            string directory,
            CancellationToken cancellationToken)
        {
            FileStream rootLease = await OpenAsync(
                Path.Combine(rootDirectory, RootLockName), FileAccess.Read, FileShare.Read,
                cancellationToken).ConfigureAwait(false);
            FileStream? fileLease = null;
            try
            {
                fileLease = await OpenAsync(
                    directory + ".lock", FileAccess.ReadWrite, FileShare.None,
                    cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(fileLease.Name, DateTime.UtcNow);
                return new DownloadCacheLease(rootLease, fileLease);
            }
            catch (Exception exception)
            {
                Trace.TraceInformation("Download cache reservation was not acquired: {0}", exception.Message);
                if (fileLease is not null)
                {
                    await fileLease.DisposeAsync().ConfigureAwait(false);
                }
                await rootLease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public static FileStream? TryAcquireCleanup(string rootDirectory)
        {
            try
            {
                return new FileStream(Path.Combine(rootDirectory, RootLockName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                Trace.TraceInformation("Download cache cleanup deferred while another process is active.");
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _fileLease.DisposeAsync().ConfigureAwait(false);
            await _rootLease.DisposeAsync().ConfigureAwait(false);
        }

        private static async Task<FileStream> OpenAsync(
            string path, FileAccess access, FileShare share, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, access, share);
                }
                catch (IOException exception) when (IsSharingViolation(exception))
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool IsSharingViolation(IOException exception)
        {
            int error = exception.HResult & 0xFFFF;
            return error is 11 or 32 or 33;
        }
    }
}
