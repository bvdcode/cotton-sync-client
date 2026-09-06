// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;

namespace Cotton.Sync.Local
{
    internal static class LocalFileDeletionFinalizer
    {
        public static async Task CompleteAsync(
            string targetPath,
            string preservedPath,
            string relativePath,
            LocalFileSnapshot expected,
            CancellationToken cancellationToken)
        {
            FileStream? verification = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateMetadata(preservedPath, relativePath, expected);
                if (!expected.IsCloudFilesOnlineOnlyPlaceholder)
                {
                    if (string.IsNullOrWhiteSpace(expected.ContentHash))
                    {
                        throw new LocalFileUnavailableException(relativePath, targetPath, "the expected local content hash is unavailable.");
                    }

                    verification = new FileStream(
                        preservedPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        bufferSize: 128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    byte[] hash = await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(Convert.ToHexStringLower(hash), expected.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LocalFileUnavailableException(relativePath, targetPath, "the local content changed before its remote deletion was applied.");
                    }
                }

                ValidateMetadata(preservedPath, relativePath, expected);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Restore(targetPath, preservedPath, relativePath, exception);
                if (exception is OperationCanceledException)
                {
                    throw;
                }

                if (exception is LocalFileUnavailableException unavailable)
                {
                    throw new LocalFileUnavailableException(relativePath, targetPath, unavailable.Reason);
                }

                throw new LocalFileUnavailableException(relativePath, targetPath, exception);
            }
            finally
            {
                if (verification is not null)
                {
                    await verification.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public static void ValidateMetadata(string path, string relativePath, LocalFileSnapshot expected)
        {
            FileInfo file = new(path);
            LocalFileMetadata metadata = LocalFileContentHasher.ReadMetadata(file, relativePath);
            FileAttributes attributes = file.Attributes;
            bool cloud = LocalFilePlatformProbe.IsCloudFilesPlaceholder(file, attributes);
            bool onlineOnly = cloud && LocalFilePlatformProbe.IsCloudFilesOnlineOnlyAttributes(attributes);
            if ((attributes & FileAttributes.Directory) != 0
                || cloud != expected.IsCloudFilesPlaceholder
                || onlineOnly != expected.IsCloudFilesOnlineOnlyPlaceholder
                || ((attributes & FileAttributes.ReparsePoint) != 0 && !cloud)
                || metadata.Length != expected.SizeBytes
                || metadata.LastWriteUtc != expected.LastWriteUtc.ToUniversalTime())
            {
                throw new LocalFileUnavailableException(relativePath, path, "the local file changed before its remote deletion was applied.");
            }
        }

        private static void Restore(string targetPath, string preservedPath, string relativePath, Exception failure)
        {
            Trace.TraceWarning("Local deletion verification failed for '{0}': {1}", relativePath, failure.Message);
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    LinuxAtomicFileCommit.MoveNoReplace(preservedPath, targetPath);
                }
                else
                {
                    File.Move(preservedPath, targetPath, overwrite: false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not restore local file '{0}'; its displaced version remains at '{1}': {2}",
                    relativePath, preservedPath, exception.Message);
                throw new LocalFileUnavailableException(relativePath, targetPath,
                    new IOException("The displaced local version is retained at '" + preservedPath + "'.", exception));
            }
        }
    }
}
