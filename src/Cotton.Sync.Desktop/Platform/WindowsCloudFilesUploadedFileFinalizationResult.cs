// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal record WindowsCloudFilesUploadedFileFinalizationResult(
        bool IsFinalized,
        long LocalSizeBytes,
        DateTime LocalLastWriteUtc);
}
