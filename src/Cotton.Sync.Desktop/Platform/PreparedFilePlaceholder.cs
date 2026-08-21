// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal record PreparedFilePlaceholder(
        int Index,
        string SyncPairId,
        string LocalRootPath,
        string NormalizedRelativePath,
        WindowsCloudFilesNativePlaceholder Placeholder,
        string FullPlaceholderPath,
        bool UpdateExistingPlaceholder,
        byte[] FileIdentity,
        SyncPlaceholderHydrationState? ExistingHydrationState);
}
