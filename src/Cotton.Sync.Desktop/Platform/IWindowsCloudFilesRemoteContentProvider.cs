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
}
