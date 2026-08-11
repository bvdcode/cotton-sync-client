// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal enum WindowsVirtualFilesAvailabilityRecoveryOutcome
    {
        Ignored = 0,
        DirectoryTracked = 1,
        AlreadyHydrated = 2,
        Hydrated = 3,
    }
}
