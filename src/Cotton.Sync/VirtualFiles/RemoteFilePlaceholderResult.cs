// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Describes the local placeholder metadata produced by a virtual-files writer.
    /// </summary>
    public sealed record RemoteFilePlaceholderResult(
        byte[]? PlaceholderIdentity,
        SyncPlaceholderHydrationState HydrationState = SyncPlaceholderHydrationState.RemoteOnly);
}
