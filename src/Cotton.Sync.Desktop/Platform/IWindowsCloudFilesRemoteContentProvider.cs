// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesRemoteContentProvider
    {
        Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads byte ranges whose integrity is already guaranteed by the remote provider.
    /// </summary>
    internal interface IWindowsCloudFilesVerifiedRangeContentProvider
    {
        Task DownloadVerifiedRangeAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            long offset,
            long length,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default);
    }
}
