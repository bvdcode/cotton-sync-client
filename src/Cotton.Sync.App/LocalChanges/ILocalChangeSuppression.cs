// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Tracks filesystem changes that were produced by the sync provider itself.
    /// </summary>
    public interface ILocalChangeSuppression
    {
        /// <summary>
        /// Suppresses the near-term watcher events produced while the provider writes a remote-backed path.
        /// </summary>
        void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath);

        /// <summary>
        /// Suppresses watcher overflow and provider-generated Cloud Files echoes while a large provider write is active.
        /// </summary>
        IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath);

        /// <summary>
        /// Returns whether the watcher event should be ignored as provider-originated churn.
        /// </summary>
        bool ShouldSuppress(LocalSyncRootChange change);
    }
}
