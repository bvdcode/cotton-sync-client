// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record DirectoryReconciliationContext(
        SyncPair SyncPair,
        SyncRunOptions Options,
        SyncRunResult Result,
        IReadOnlyList<string> PathKeys,
        IReadOnlyDictionary<string, LocalDirectorySnapshot> LocalByPath,
        IDictionary<string, RemoteDirectorySnapshot> RemoteByPath,
        IReadOnlyDictionary<string, SyncStateEntry> StateByPath,
        NodeDto RemoteRootNode,
        DateTime StartedAtUtc,
        CancellationToken CancellationToken);
}
