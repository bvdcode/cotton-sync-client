// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record OnlineOnlyPlaceholderMoveContext(
        SyncPair SyncPair,
        SyncRunOptions Options,
        SyncRunResult Result,
        IReadOnlyDictionary<string, LocalFileSnapshot> LocalByPath,
        IDictionary<string, RemoteFileSnapshot> RemoteByPath,
        IDictionary<string, SyncStateEntry> StateByPath,
        CancellationToken CancellationToken);
}
