// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal record ShellShareLinkSmokeData(
        string SyncedFilePath,
        string RemoteOnlyPlaceholderPath,
        string HydratedPlaceholderPath,
        string DirectoryPath,
        string LocalOnlyPath);
}
