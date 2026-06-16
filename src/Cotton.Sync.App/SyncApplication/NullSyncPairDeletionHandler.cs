// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.SyncApplication
{
    internal sealed class NullSyncPairDeletionHandler : ISyncPairDeletionHandler
    {
        public static NullSyncPairDeletionHandler Instance { get; } = new();

        private NullSyncPairDeletionHandler()
        {
        }

        public Task BeforeDeleteAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
