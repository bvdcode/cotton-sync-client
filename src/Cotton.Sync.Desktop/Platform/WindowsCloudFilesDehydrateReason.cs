// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal enum WindowsCloudFilesDehydrateReason
    {
        Never = 0,
        UserManual = 1,
        SystemInactivity = 2,
        SystemLowSpace = 3,
        SystemOsUpgrade = 4,
    }
}
