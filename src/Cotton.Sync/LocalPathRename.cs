// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync
{
    /// <summary>
    /// Identifies one observed rename within a local sync root.
    /// </summary>
    public class LocalPathRename
    {
        /// <summary>
        /// Creates a rename with normalized relative paths, including intermediate temporary names.
        /// </summary>
        public LocalPathRename(string sourcePath, string targetPath)
        {
            SourcePath = SyncPath.Normalize(sourcePath);
            TargetPath = SyncPath.Normalize(targetPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(TargetPath);
        }

        /// <summary>
        /// Gets the path before the rename.
        /// </summary>
        public string SourcePath { get; }

        /// <summary>
        /// Gets the path after the rename.
        /// </summary>
        public string TargetPath { get; }
    }
}
