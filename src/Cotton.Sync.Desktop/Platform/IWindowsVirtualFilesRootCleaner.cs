// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsVirtualFilesRootCleaner
    {
        WindowsVirtualFilesRootCleanupDecision EvaluateBeforeUnregister(SyncPairSettings syncPair);

        Task<WindowsVirtualFilesRootCleanupResult> CleanupAfterUnregisterAsync(
            WindowsVirtualFilesRootCleanupDecision decision,
            CancellationToken cancellationToken = default);
    }
}
