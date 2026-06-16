// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed record DesktopUpdateCheckResult(
        DesktopReleaseManifest Manifest,
        DesktopSemanticVersion CurrentVersion,
        DesktopSemanticVersion LatestVersion,
        bool IsUpdateAvailable,
        DesktopReleaseAsset? InstallerAsset)
    {
        public bool CanDownloadInstaller => IsUpdateAvailable && InstallerAsset is not null;
    }
}
