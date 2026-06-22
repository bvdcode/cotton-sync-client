// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal sealed record DesktopUpdateStatusSnapshot(
        string CurrentVersion,
        string? LatestVersion,
        bool IsUpdateAvailable,
        bool IsInstallerReady,
        string Details,
        string? InstallerPath,
        Uri? ReleaseUrl);
}
