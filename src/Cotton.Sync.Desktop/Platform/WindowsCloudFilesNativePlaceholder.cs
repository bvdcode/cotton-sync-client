// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal record WindowsCloudFilesNativePlaceholder(
        string BaseDirectoryPath,
        string RelativeFileName,
        byte[] FileIdentity,
        long FileSizeBytes,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        bool IsDirectory = false);
}
