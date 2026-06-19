// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Describes progress while scanning a remote Cotton folder tree.
    /// </summary>
    public class RemoteTreeScanProgress
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTreeScanProgress" /> class.
        /// </summary>
        public RemoteTreeScanProgress(
            int filesScanned,
            int directoriesScanned,
            string? currentPath,
            int pagesScanned = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(filesScanned);
            ArgumentOutOfRangeException.ThrowIfNegative(directoriesScanned);
            ArgumentOutOfRangeException.ThrowIfNegative(pagesScanned);
            FilesScanned = filesScanned;
            DirectoriesScanned = directoriesScanned;
            CurrentPath = string.IsNullOrWhiteSpace(currentPath) ? string.Empty : SyncPath.Normalize(currentPath);
            PagesScanned = pagesScanned;
        }

        /// <summary>
        /// Gets the number of remote file entries discovered so far.
        /// </summary>
        public int FilesScanned { get; }

        /// <summary>
        /// Gets the number of remote directory entries discovered so far.
        /// </summary>
        public int DirectoriesScanned { get; }

        /// <summary>
        /// Gets the number of remote child pages loaded so far.
        /// </summary>
        public int PagesScanned { get; }

        /// <summary>
        /// Gets the most recent discovered remote path when available.
        /// </summary>
        public string CurrentPath { get; }
    }
}
