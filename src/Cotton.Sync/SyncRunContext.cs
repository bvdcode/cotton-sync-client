// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record SyncRunContext(
        SyncPair SyncPair,
        SyncRunOptions Options,
        SyncRunResult Result,
        SyncTreeLookups TreeLookups,
        Dictionary<string, SyncStateEntry> DirectoryStateByPath,
        Dictionary<string, SyncStateEntry> FileStateByPath,
        ScopedVirtualFilesDirectoryRenamePlan? ScopedDirectoryRename,
        DateTime StartedAtUtc,
        CancellationToken CancellationToken)
    {
        public Dictionary<string, LocalDirectorySnapshot> LocalDirectoriesByPath =>
            TreeLookups.LocalDirectoriesByPath;

        public Dictionary<string, RemoteDirectorySnapshot> RemoteDirectoriesByPath =>
            TreeLookups.RemoteDirectoriesByPath;

        public Dictionary<string, LocalFileSnapshot> LocalFilesByPath => TreeLookups.LocalFilesByPath;

        public Dictionary<string, RemoteFileSnapshot> RemoteFilesByPath => TreeLookups.RemoteFilesByPath;
    }
}
