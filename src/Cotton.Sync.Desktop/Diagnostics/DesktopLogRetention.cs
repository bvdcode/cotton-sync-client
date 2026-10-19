// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal static class DesktopLogRetention
    {
        public const long MaxFileSizeBytes = 5L * 1024L * 1024L;
        public const int RetainedFileCount = 3;
    }
}
