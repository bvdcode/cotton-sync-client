// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal static class SyncRunOptionsValidator
    {
        public static void Validate(SyncRunOptions options)
        {
            ArgumentNullException.ThrowIfNull(options.Scope);
            if (options.MinimumLocalUploadAge < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Minimum local upload age cannot be negative.");
            }

            if (options.MaximumLocalDeletesPerRun < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum local deletes per run cannot be negative.");
            }

            if (options.MaximumRemoteDeletesPerRun < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum remote deletes per run cannot be negative.");
            }

            if (options.MaximumStoredResultActivities < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum stored result activities cannot be negative.");
            }

            if (options.InitialVirtualFilesPopulationQueueCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files population queue capacity must be positive.");
            }

            if (options.InitialVirtualFilesStateBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files state batch size must be positive.");
            }

            if (options.InitialVirtualFilesPlaceholderConcurrency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files placeholder concurrency must be positive.");
            }

            if (options.InitialVirtualFilesPlaceholderBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files placeholder batch size must be positive.");
            }
        }
    }
}
