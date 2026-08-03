// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Globalization;
using Cotton.Sync.State;

namespace Cotton.Sync.Local
{
    /// <summary>
    /// Writes synchronized local files through temporary files under the sync metadata folder.
    /// </summary>
    public class AtomicLocalFileSyncWriter : ILocalFileSyncWriter
    {
        private const string DeletedDirectoryName = "deleted";
        private const string TemporaryDirectoryName = "tmp";

        /// <inheritdoc />
        public async Task WriteFileAsync(
            string rootPath,
            string relativePath,
            Func<Stream, CancellationToken, Task> writeContentAsync,
            DateTime? lastWriteUtc = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentNullException.ThrowIfNull(writeContentAsync);
            string normalizedPath = NormalizeWritablePath(relativePath);
            string fullRoot = Path.GetFullPath(rootPath);
            Directory.CreateDirectory(fullRoot);

            string targetPath = Path.Combine(fullRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            string temporaryDirectory = Path.Combine(SyncMetadataDirectory.Ensure(fullRoot), TemporaryDirectoryName);
            Directory.CreateDirectory(temporaryDirectory);
            CleanupTemporaryDownloads(temporaryDirectory);
            string temporaryPath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".download");
            bool moved = false;
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await writeContentAsync(stream, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (lastWriteUtc.HasValue)
                {
                    File.SetLastWriteTimeUtc(temporaryPath, lastWriteUtc.Value.ToUniversalTime());
                }

                File.Move(temporaryPath, targetPath, overwrite: true);
                moved = true;
            }
            finally
            {
                if (!moved && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        /// <inheritdoc />
        public Task DeleteFileAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = NormalizeWritablePath(relativePath);
            string fullRoot = Path.GetFullPath(rootPath);
            string targetPath = Path.Combine(fullRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!TryGetExistingAttributes(targetPath, out FileAttributes attributes))
            {
                return Task.CompletedTask;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("Local file delete target is a directory: " + normalizedPath);
            }

            string preservedPath = CreateDeletedPath(fullRoot, normalizedPath);
            string? preservedDirectory = Path.GetDirectoryName(preservedPath);
            if (!string.IsNullOrWhiteSpace(preservedDirectory))
            {
                SyncMetadataDirectory.Ensure(fullRoot);
                Directory.CreateDirectory(preservedDirectory);
            }

            File.Move(targetPath, preservedPath, overwrite: false);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task CreateDirectoryAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = NormalizeWritablePath(relativePath);
            string fullRoot = Path.GetFullPath(rootPath);
            Directory.CreateDirectory(Path.Combine(fullRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MoveDirectoryAsync(
            string rootPath,
            string sourceRelativePath,
            string targetRelativePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedSourcePath = NormalizeWritablePath(sourceRelativePath);
            string normalizedTargetPath = NormalizeWritablePath(targetRelativePath);
            string fullRoot = Path.GetFullPath(rootPath);
            string sourcePath = Path.Combine(
                fullRoot,
                normalizedSourcePath.Replace('/', Path.DirectorySeparatorChar));
            string targetPath = Path.Combine(
                fullRoot,
                normalizedTargetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!TryGetExistingAttributes(sourcePath, out FileAttributes sourceAttributes))
            {
                throw new DirectoryNotFoundException("Local directory move source does not exist: " + normalizedSourcePath);
            }

            if ((sourceAttributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("Local directory move source is a file: " + normalizedSourcePath);
            }

            bool samePathIgnoringCase = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
            if (!samePathIgnoringCase && TryGetExistingAttributes(targetPath, out _))
            {
                throw new IOException("Local directory move target already exists: " + normalizedTargetPath);
            }

            string? targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            if (samePathIgnoringCase)
            {
                if (string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
                {
                    return Task.CompletedTask;
                }

                string temporaryPath = Path.Combine(
                    Path.GetDirectoryName(sourcePath)!,
                    ".cotton-sync-case-rename-" + Guid.NewGuid().ToString("N"));
                Directory.Move(sourcePath, temporaryPath);
                try
                {
                    Directory.Move(temporaryPath, targetPath);
                }
                catch
                {
                    if (!Directory.Exists(sourcePath) && Directory.Exists(temporaryPath))
                    {
                        Directory.Move(temporaryPath, sourcePath);
                    }

                    throw;
                }

                return Task.CompletedTask;
            }

            Directory.Move(sourcePath, targetPath);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeleteDirectoryAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = NormalizeWritablePath(relativePath);
            string fullRoot = Path.GetFullPath(rootPath);
            string targetPath = Path.Combine(fullRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!TryGetExistingAttributes(targetPath, out FileAttributes attributes))
            {
                return Task.CompletedTask;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("Local directory delete target is a file: " + normalizedPath);
            }

            string preservedPath = CreateDeletedPath(fullRoot, normalizedPath);
            string? preservedParentDirectory = Path.GetDirectoryName(preservedPath);
            if (!string.IsNullOrWhiteSpace(preservedParentDirectory))
            {
                SyncMetadataDirectory.Ensure(fullRoot);
                Directory.CreateDirectory(preservedParentDirectory);
            }

            Directory.Move(targetPath, preservedPath);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public string CreateConflictRelativePath(string rootPath, string relativePath, DateTime timestampUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            string normalizedPath = NormalizeWritablePath(relativePath);
            string directory = Path.GetDirectoryName(normalizedPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(normalizedPath);
            string extension = Path.GetExtension(normalizedPath);
            string suffix = timestampUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ");
            for (int index = 1; index < int.MaxValue; index++)
            {
                string indexedSuffix = index == 1 ? suffix : suffix + "-" + index.ToString(CultureInfo.InvariantCulture);
                string candidateName = fileName + " (Cotton conflict " + indexedSuffix + ")" + extension;
                string candidateRelativePath = string.IsNullOrEmpty(directory)
                    ? candidateName
                    : directory.Replace(Path.DirectorySeparatorChar, '/') + "/" + candidateName;
                string candidateFullPath = Path.Combine(
                    Path.GetFullPath(rootPath),
                    candidateRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(candidateFullPath) && !Directory.Exists(candidateFullPath))
                {
                    return SyncPath.Normalize(candidateRelativePath);
                }
            }

            throw new InvalidOperationException("Unable to allocate a unique conflict file path.");
        }

        private static string NormalizeWritablePath(string relativePath)
        {
            string normalizedPath = SyncPath.Normalize(relativePath);
            if (SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
            {
                throw new ArgumentException("Ignored sync paths cannot be written by the local sync writer.", nameof(relativePath));
            }

            return normalizedPath;
        }

        private static bool TryGetExistingAttributes(string fullPath, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(fullPath);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }

        private static void CleanupTemporaryDownloads(string temporaryDirectory)
        {
            foreach (string temporaryFile in Directory.EnumerateFiles(temporaryDirectory, "*.download"))
            {
                try
                {
                    File.Delete(temporaryFile);
                }
                catch (IOException exception)
                {
                    Trace.TraceWarning("Failed to delete stale sync download temp file '{0}': {1}", temporaryFile, exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    Trace.TraceWarning("Failed to delete stale sync download temp file '{0}': {1}", temporaryFile, exception.Message);
                }
            }
        }

        private static string CreateDeletedPath(string fullRoot, string normalizedPath)
        {
            string quarantineName = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
                + "-"
                + Guid.NewGuid().ToString("N");
            return Path.Combine(
                fullRoot,
                SyncMetadataDirectory.Name,
                DeletedDirectoryName,
                quarantineName,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
