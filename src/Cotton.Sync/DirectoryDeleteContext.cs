// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record DirectoryDeleteContext(
        SyncPair SyncPair,
        SyncRunOptions Options,
        SyncRunResult Result,
        SyncDeleteGuard DeleteGuard,
        IReadOnlyList<string> PathKeys,
        IReadOnlyDictionary<string, LocalDirectorySnapshot> LocalByPath,
        IReadOnlyDictionary<string, RemoteDirectorySnapshot> RemoteByPath,
        IReadOnlyDictionary<string, SyncStateEntry> StateByPath,
        IReadOnlyDictionary<string, LocalFileSnapshot> LocalFilesByPath,
        IReadOnlyDictionary<string, RemoteFileSnapshot> RemoteFilesByPath,
        IReadOnlyDictionary<string, SyncStateEntry> FileStateByPath,
        DirectoryContentIndex LocalContentIndex,
        DirectoryContentIndex RemoteContentIndex,
        IReadOnlySet<string>? ScopedDeleteKeys,
        IReadOnlyList<string>? PlannedScopedDeleteKeys,
        CancellationToken CancellationToken);
}
