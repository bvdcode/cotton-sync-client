// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Detects local changes that occurred while filesystem watchers were not running.
    /// </summary>
    public interface ILocalOfflineChangeDetector
    {
        /// <summary>
        /// Creates a scoped sync request for local changes not represented by durable sync state.
        /// </summary>
        Task<SyncRunRequest?> DetectAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken = default);
    }
}
