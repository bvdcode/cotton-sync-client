// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesHydrator
    {
        Task HydratePlaceholderAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken = default);
    }
}
