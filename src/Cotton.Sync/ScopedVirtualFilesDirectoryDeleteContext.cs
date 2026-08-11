// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record ScopedVirtualFilesDirectoryDeleteContext(
        IReadOnlyDictionary<string, LocalDirectorySnapshot> LocalDirectoriesByPath,
        IReadOnlyDictionary<string, RemoteDirectorySnapshot> RemoteDirectoriesByPath,
        IReadOnlyDictionary<string, LocalFileSnapshot> LocalFilesByPath,
        IReadOnlyDictionary<string, RemoteFileSnapshot> RemoteFilesByPath,
        IReadOnlyDictionary<string, SyncStateEntry> DirectoryStateByPath,
        IReadOnlyDictionary<string, SyncStateEntry> FileStateByPath);
}
