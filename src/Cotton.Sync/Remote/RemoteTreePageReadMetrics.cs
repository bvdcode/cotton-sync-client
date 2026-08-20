// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal record struct RemoteTreePageReadMetrics(
        int PagesScanned,
        TimeSpan PageReadLatencyTotal,
        TimeSpan PageReadLatencyMax,
        TimeSpan LastPageReadLatency)
    {
        public static RemoteTreePageReadMetrics Empty { get; } =
            new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    }
}
