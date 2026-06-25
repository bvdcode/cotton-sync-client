// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsShellChangeNotifier
    {
        void NotifyItemUpdated(string path);

        void NotifyDirectoryUpdated(string path);
    }
}
