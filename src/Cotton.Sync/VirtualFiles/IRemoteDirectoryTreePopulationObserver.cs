// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Observes completion of a provider-originated remote directory tree materialization.
    /// </summary>
    public interface IRemoteDirectoryTreePopulationObserver
    {
        /// <summary>
        /// Runs after the sync engine has materialized directories and child placeholders for a remote directory tree.
        /// </summary>
        Task AfterDirectoryTreePopulationAsync(
            IReadOnlyList<RemoteDirectoryMaterializationRequest> directories,
            CancellationToken cancellationToken = default);
    }
}
