// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesDehydrateCompletionNotification(
        WindowsCloudFilesConnectionKey ConnectionKey,
        WindowsCloudFilesTransferKey TransferKey,
        WindowsCloudFilesRequestKey RequestKey,
        byte[] FileIdentity,
        string? NormalizedPath,
        WindowsCloudFilesDehydrateReason Reason,
        bool IsBackground,
        bool WasHydrated);
}
