// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesFetchDataRequest(
        WindowsCloudFilesConnectionKey ConnectionKey,
        WindowsCloudFilesTransferKey TransferKey,
        WindowsCloudFilesRequestKey RequestKey,
        byte[] FileIdentity,
        long FileSizeBytes,
        long RequiredOffset,
        long RequiredLength,
        long OptionalOffset,
        long OptionalLength,
        string? NormalizedPath,
        byte PriorityHint,
        WindowsCloudFilesProcessInfo? ProcessInfo = null);
}
