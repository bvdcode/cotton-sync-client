// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Cli
{
    internal class SyncCliSoakSettings(
        SyncCliConnectionOptions connectionOptions,
        SyncCliConnectionOptions? secondConnectionOptions,
        int? iterations,
        int? durationSeconds,
        int intervalSeconds,
        string? probeFile)
    {
        public SyncCliConnectionOptions ConnectionOptions { get; } = connectionOptions;

        public int? DurationSeconds { get; } = durationSeconds;

        public int IntervalSeconds { get; } = intervalSeconds;

        public int? Iterations { get; } = iterations;

        public string? ProbeFile { get; } = probeFile;

        public SyncCliConnectionOptions? SecondConnectionOptions { get; } = secondConnectionOptions;
    }
}
