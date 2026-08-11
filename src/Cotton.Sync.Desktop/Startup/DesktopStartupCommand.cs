// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal enum DesktopStartupCommand
    {
        None = 0,
        SelfTest = 1,
        LiveSyncSmoke = 2,
        WindowsVirtualFilesSmoke = 3,
        UpdateDiscoverySmoke = 4,
        UpdateInstallSmoke = 5,
        ShellShareLinkSmoke = 6,
        SocketCleanupSmoke = 7,
        ResolveShellShareLink = 8,
        CopyShellShareLink = 9,
        CleanupCloudFiles = 10,
        ExportDiagnostics = 11,
    }
}
