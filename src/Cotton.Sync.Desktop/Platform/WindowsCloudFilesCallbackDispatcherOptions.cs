// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal record WindowsCloudFilesCallbackDispatcherOptions(
        int MaxConcurrentFetches,
        int QueueCapacity,
        TimeSpan ShutdownTimeout = default)
    {
        public static WindowsCloudFilesCallbackDispatcherOptions Default { get; } =
            new(MaxConcurrentFetches: 4, QueueCapacity: 1024, ShutdownTimeout: TimeSpan.FromSeconds(5));

        public WindowsCloudFilesCallbackDispatcherOptions Normalize()
        {
            TimeSpan shutdownTimeout = ShutdownTimeout > TimeSpan.Zero
                ? ShutdownTimeout
                : WindowsCloudFilesCallbackDispatcherOptions.Default.ShutdownTimeout;
            return new WindowsCloudFilesCallbackDispatcherOptions(
                Math.Max(1, MaxConcurrentFetches),
                Math.Max(1, QueueCapacity),
                shutdownTimeout);
        }
    }
}
