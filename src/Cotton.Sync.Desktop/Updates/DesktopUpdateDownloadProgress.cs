// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed record DesktopUpdateDownloadProgress(
        string Version,
        string AssetName,
        long BytesDownloaded,
        long? TotalBytes);
}
