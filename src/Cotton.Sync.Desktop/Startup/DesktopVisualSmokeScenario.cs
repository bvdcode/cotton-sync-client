// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal enum DesktopVisualSmokeScenario
    {
        SignInError,
        AddFolder,
        AddFolderManyRemoteFolders,
        EmptyDashboard,
        Dashboard,
        FolderControls,
        Progress,
        LongProgress,
        ManySmallDownload,
        HighPressureStarting,
        Settings,
        SettingsDiagnostics,
        Error,
        Offline,
        MissingLocalRoot,
        Conflict,
    }
}
