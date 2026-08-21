// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;

namespace Cotton.Sync.Local
{
    internal static class LocalFileContentHasher
    {
        private const int BufferSize = 1024 * 128;
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

        public static async Task<string> ComputeAsync(
            string filePath,
            string relativePath,
            IProgress<SyncTransferProgress>? progress,
            long? totalBytes,
            CancellationToken cancellationToken)
        {
            try
            {
                long bytesRead = 0;
                DateTime lastReportedAtUtc = DateTime.UtcNow;
                ReportProgress(progress, relativePath, bytesRead, totalBytes, isCompleted: false);
                await using FileStream stream = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hasher.AppendData(buffer.AsSpan(0, read));
                    bytesRead += read;
                    DateTime now = DateTime.UtcNow;
                    if (now - lastReportedAtUtc >= ProgressInterval)
                    {
                        ReportProgress(progress, relativePath, bytesRead, totalBytes, isCompleted: false);
                        lastReportedAtUtc = now;
                    }
                }

                byte[] hash = hasher.GetHashAndReset();
                ReportProgress(progress, relativePath, bytesRead, totalBytes, isCompleted: true);
                return Convert.ToHexStringLower(hash);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new LocalFilePermissionDeniedException(relativePath, filePath, exception);
            }
            catch (IOException exception)
            {
                throw new LocalFileUnavailableException(relativePath, filePath, exception);
            }
        }

        public static LocalFileMetadata ReadMetadata(FileInfo file, string relativePath)
        {
            try
            {
                file.Refresh();
                if (!file.Exists)
                {
                    throw new FileNotFoundException("Local file disappeared during scanning.", file.FullName);
                }

                return new LocalFileMetadata(file.Length, file.LastWriteTimeUtc);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new LocalFilePermissionDeniedException(relativePath, file.FullName, exception);
            }
            catch (IOException exception)
            {
                throw new LocalFileUnavailableException(relativePath, file.FullName, exception);
            }
        }

        private static void ReportProgress(
            IProgress<SyncTransferProgress>? progress,
            string relativePath,
            long processedBytes,
            long? totalBytes,
            bool isCompleted)
        {
            if (progress is null)
            {
                return;
            }

            long boundedBytes = totalBytes.HasValue && processedBytes > totalBytes.Value
                ? totalBytes.Value
                : processedBytes;
            progress.Report(new SyncTransferProgress(
                SyncTransferDirection.Hash,
                relativePath,
                boundedBytes,
                totalBytes,
                isCompleted));
        }
    }
}
