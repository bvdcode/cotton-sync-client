// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Local
{
    internal static class LocalDownloadLease
    {
        private const string LockFileName = "downloads.lock";

        public static FileStream Acquire(string metadataDirectory, string relativePath, string targetPath)
        {
            try
            {
                return new FileStream(
                    Path.Combine(metadataDirectory, LockFileName),
                    FileMode.OpenOrCreate,
                    FileAccess.Read,
                    FileShare.Read);
            }
            catch (IOException exception)
            {
                Trace.TraceWarning("Could not reserve the local download for '{0}': {1}", relativePath, exception.Message);
                throw new LocalFileUnavailableException(relativePath, targetPath, exception);
            }
        }

        public static FileStream? TryAcquireCleanup(string metadataDirectory)
        {
            try
            {
                return new FileStream(
                    Path.Combine(metadataDirectory, LockFileName),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception)
            {
                Trace.TraceInformation("Download cleanup deferred while the reservation is unavailable: {0}", exception.Message);
                return null;
            }
        }
    }
}
