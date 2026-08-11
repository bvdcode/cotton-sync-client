// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record SyncFileReconciliationContext(
        SyncPair SyncPair,
        SyncRunOptions Options,
        SyncRunResult Result,
        SyncDeleteGuard DeleteGuard,
        IReadOnlySet<string>? ScopedFileDeleteKeys,
        IReadOnlySet<string> ScopedLocalDeletedFileKeys,
        SyncStateEntry State,
        string RelativePath,
        LocalFileSnapshot? Local,
        RemoteFileSnapshot? Remote,
        CancellationToken CancellationToken)
    {
        public string PathKey { get; } = SyncPath.ToKey(RelativePath);

        public bool IsExactLocalDelete => ScopedLocalDeletedFileKeys.Contains(PathKey);
    }
}
