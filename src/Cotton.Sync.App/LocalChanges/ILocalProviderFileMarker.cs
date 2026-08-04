// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Persists the identity of local files materialized by the provider but intentionally left outside remote state.
    /// </summary>
    public interface ILocalProviderFileMarker
    {
        /// <summary>
        /// Records a provider-created file after its content has been written successfully.
        /// </summary>
        Task MarkAsync(
            Guid syncPairId,
            string localRootPath,
            string relativePath,
            string contentHash,
            long sizeBytes,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns whether an untracked local file is the same unchanged provider-created file.
        /// </summary>
        Task<bool> IsUnchangedAsync(
            Guid syncPairId,
            string localRootPath,
            LocalFileSnapshot localFile,
            CancellationToken cancellationToken = default);
    }
}
