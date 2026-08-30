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
        /// Suppresses hydration echoes only while the provider-backed path remains pinned.
        /// </summary>
        void SuppressProviderPinnedWrite(Guid syncPairId, string localRootPath, string relativePath);

        /// <summary>
        /// Suppresses provider-created directory echoes only while the directory remains unpinned.
        /// </summary>
        void SuppressProviderDirectoryWrite(Guid syncPairId, string localRootPath, string relativePath);

        /// <summary>
        /// Suppresses only the watcher event that exposes a newly materialized provider file.
        /// </summary>
        void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath);

        /// <summary>
        /// Suppresses the complete creation/finalization event burst while the provider-written file still matches its baseline metadata.
        /// </summary>
        void SuppressProviderFileMaterialization(
            Guid syncPairId,
            string localRootPath,
            string relativePath,
            long expectedSizeBytes,
            DateTime? expectedLastWriteUtc)
        {
            SuppressProviderFileCreation(syncPairId, localRootPath, relativePath);
        }

        /// <summary>
        /// Suppresses provider metadata echoes without hiding a subsequent user delete or move.
        /// </summary>
        void SuppressProviderMetadataWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(syncPairId, localRootPath, relativePath);
        }

        /// <summary>
        /// Suppresses placeholder-creation echoes only while the resulting path remains online-only.
        /// </summary>
        void SuppressProviderOnlineOnlyWrite(Guid syncPairId, string localRootPath, string relativePath)
        {
            SuppressProviderWrite(syncPairId, localRootPath, relativePath);
        }

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
