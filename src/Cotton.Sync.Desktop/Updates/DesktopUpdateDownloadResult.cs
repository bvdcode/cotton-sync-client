// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed record DesktopUpdateDownloadResult(
        DesktopReleaseManifest Manifest,
        DesktopReleaseAsset InstallerAsset,
        string FilePath,
        string Sha256,
        long SizeBytes);
}
