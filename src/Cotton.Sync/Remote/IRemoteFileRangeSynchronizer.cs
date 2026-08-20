// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Downloads verified byte ranges for virtual-file hydration.
    /// </summary>
    public interface IRemoteFileRangeSynchronizer : IRemoteFileSynchronizer
    {
        /// <summary>
        /// Downloads a remote file byte range and reports byte-level download progress.
        /// </summary>
        Task DownloadFileRangeAsync(
            Guid nodeFileId,
            string relativePath,
            long offset,
            long length,
            string? expectedETag,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress,
            CancellationToken cancellationToken = default);
    }
}
