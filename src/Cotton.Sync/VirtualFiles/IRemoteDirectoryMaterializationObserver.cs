// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Observes provider-originated remote directory materialization before the local filesystem is changed.
    /// </summary>
    public interface IRemoteDirectoryMaterializationObserver
    {
        /// <summary>
        /// Runs before the sync engine creates a local directory that represents a remote directory.
        /// </summary>
        Task BeforeCreateDirectoryAsync(
            RemoteDirectoryMaterializationRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs after the sync engine creates a local directory that represents a remote directory.
        /// </summary>
        Task AfterCreateDirectoryAsync(
            RemoteDirectoryMaterializationRequest request,
            CancellationToken cancellationToken = default);
    }
}
