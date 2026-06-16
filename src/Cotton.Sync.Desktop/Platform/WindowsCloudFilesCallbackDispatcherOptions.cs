// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesCallbackDispatcherOptions(
        int MaxConcurrentFetches,
        int QueueCapacity)
    {
        public static WindowsCloudFilesCallbackDispatcherOptions Default { get; } =
            new(MaxConcurrentFetches: 4, QueueCapacity: 1024);

        public WindowsCloudFilesCallbackDispatcherOptions Normalize()
        {
            return new WindowsCloudFilesCallbackDispatcherOptions(
                Math.Max(1, MaxConcurrentFetches),
                Math.Max(1, QueueCapacity));
        }
    }
}
