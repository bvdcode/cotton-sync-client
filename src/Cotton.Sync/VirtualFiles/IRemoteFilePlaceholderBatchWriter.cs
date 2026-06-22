// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Creates batches of local virtual-files placeholders for high-scale initial population.
    /// </summary>
    public interface IRemoteFilePlaceholderBatchWriter : IRemoteFilePlaceholderWriter
    {
        /// <summary>
        /// Creates or updates placeholders for the supplied remote files.
        /// </summary>
        Task<IReadOnlyList<RemoteFilePlaceholderBatchResult>> CreatePlaceholdersAsync(
            IReadOnlyList<RemoteFilePlaceholderRequest> requests,
            CancellationToken cancellationToken = default);
    }
}
