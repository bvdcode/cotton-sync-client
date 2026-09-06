// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Local
{
    /// <summary>
    /// Writes and deletes local files for the synchronization engine.
    /// </summary>
    public interface ILocalFileSyncWriter
    {
        /// <summary>
        /// Writes a file through a temporary path. A null expected hash requires an absent target;
        /// replacement preserves the displaced version when its content differs from the expected hash.
        /// </summary>
        Task<LocalFileWriteResult> WriteFileAsync(
            string rootPath,
            string relativePath,
            Func<Stream, CancellationToken, Task> writeContentAsync,
            DateTime? lastWriteUtc = null,
            string? expectedLocalContentHash = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves the expected local file out of the sync tree, restoring changed content.
        /// A null snapshot requires the target to remain absent.
        /// </summary>
        Task DeleteFileAsync(
            string rootPath,
            string relativePath,
            LocalFileSnapshot? expectedLocalFile,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a local directory if it does not exist.
        /// </summary>
        Task CreateDirectoryAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves a local directory and its complete subtree to a new relative path.
        /// </summary>
        Task MoveDirectoryAsync(
            string rootPath,
            string sourceRelativePath,
            string targetRelativePath,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Moves a local directory out of the sync tree if it exists.
        /// </summary>
        Task DeleteDirectoryAsync(string rootPath, string relativePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a unique conflict-copy relative path for a file.
        /// </summary>
        string CreateConflictRelativePath(string rootPath, string relativePath, DateTime timestampUtc);
    }
}
