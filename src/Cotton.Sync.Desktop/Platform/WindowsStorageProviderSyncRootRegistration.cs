// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal record WindowsStorageProviderSyncRootRegistration(
        Guid SyncPairId,
        string LocalRootPath,
        string ProviderVersion,
        string IconResource);
}
