// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.SyncApplication
{
    /// <summary>
    /// Cleans up platform-owned resources before a sync pair is deleted from durable settings.
    /// </summary>
    public interface ISyncPairDeletionHandler
    {
        Task BeforeDeleteAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default);
    }
}
