// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal enum DesktopTrayActivityKind
    {
        None = 0,
        Syncing = 1,
        Uploading = 2,
        Downloading = 3,
        MakingAvailable = 4,
        FreeingSpace = 5,
    }
}
