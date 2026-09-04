// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;

namespace Cotton.Sync.Local
{
    internal static class LocalFileReplacementFinalizer
    {
        public static async Task<LocalFileWriteResult> CompleteAsync(
            string rootPath,
            string relativePath,
            string previousPath,
            string expectedContentHash,
            Func<string> createConflictRelativePath,
            CancellationToken cancellationToken)
        {
            try
            {
                await using FileStream previous = new(
                    previousPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] hash = await SHA256.HashDataAsync(previous, cancellationToken).ConfigureAwait(false);
                string contentHash = Convert.ToHexStringLower(hash);

                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(contentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        File.Delete(previousPath);
                    }

                    return new LocalFileWriteResult();
                }

                string conflictRelativePath = createConflictRelativePath();
                string conflictFullPath = Path.Combine(rootPath, conflictRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (OperatingSystem.IsLinux())
                {
                    LinuxAtomicFileCommit.MoveNoReplace(previousPath, conflictFullPath);
                }
                else
                {
                    File.Move(previousPath, conflictFullPath, overwrite: false);
                }
                return new LocalFileWriteResult(conflictRelativePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                string preservedRelativePath = Path.GetRelativePath(rootPath, previousPath);
                Trace.TraceWarning(
                    "Failed to finalize local file replacement for '{0}'; the displaced version remains at '{1}': {2}",
                    relativePath,
                    preservedRelativePath,
                    exception.Message);
                throw new LocalFileUnavailableException(
                    relativePath,
                    Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                    new IOException("The replaced local version is retained at '" + preservedRelativePath + "'.", exception));
            }
        }
    }
}
