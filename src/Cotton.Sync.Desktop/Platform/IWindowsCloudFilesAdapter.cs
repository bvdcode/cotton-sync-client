// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesAdapter
    {
        RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request);
    }
}
