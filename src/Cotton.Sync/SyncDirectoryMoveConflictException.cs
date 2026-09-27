// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    /// <summary>
    /// Represents a remote folder move blocked by an independently existing local folder.
    /// </summary>
    public class SyncDirectoryMoveConflictException : IOException
    {
        public SyncDirectoryMoveConflictException(string sourcePath, string targetPath)
            : base($"Cloud folder '{sourcePath}' moved to '{targetPath}', but the destination already exists locally. Both local folders were kept. Resolve the folder collision, then retry sync.")
        {
            SourcePath = sourcePath;
            TargetPath = targetPath;
        }

        public string SourcePath { get; }

        public string TargetPath { get; }
    }
}
