// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal record DesktopCloudFilesSyncPairRegistrationSnapshot(
        Guid SyncPairId,
        string DisplayName,
        string LocalRootPath,
        bool IsEnabled,
        bool IsExpectedRegistered,
        bool? IsRegistered,
        string Status,
        string Details);
}
