// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Threading.Channels;

namespace Cotton.Sync
{
    internal record InitialVirtualFilesPopulationContext(
        SyncPair SyncPair,
        SyncRunOptions Options,
        SyncRunResult Result,
        ChannelReader<InitialVirtualFilesPopulationItem> Reader,
        DateTime StartedAtUtc,
        InitialVirtualFilesStreamingPlan StreamingPlan,
        InitialVirtualFilesPopulationMetrics Metrics,
        CancellationToken CancellationToken);
}
