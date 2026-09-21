// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal record WindowsCloudFilesProviderProgress(
        WindowsCloudFilesConnectionKey ConnectionKey,
        WindowsCloudFilesTransferKey TransferKey,
        long TotalBytes,
        long CompletedBytes);
}
