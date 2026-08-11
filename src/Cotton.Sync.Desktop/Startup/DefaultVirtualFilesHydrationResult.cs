// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Startup
{
    internal record DefaultVirtualFilesHydrationResult(
        int Failures,
        WindowsCloudFilesConnection Connection);
}
