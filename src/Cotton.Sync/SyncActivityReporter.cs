// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal static class SyncActivityReporter
    {
        public static void Record(
            SyncRunResult result,
            SyncRunOptions options,
            SyncActivityKind kind,
            string relativePath,
            string? details,
            bool requiresUserAction = false,
            bool publishActivityProgress = true)
        {
            SyncActivity activity = new SyncActivity
            {
                Kind = kind,
                RelativePath = SyncPath.Normalize(relativePath),
                Details = details,
                RequiresUserAction = requiresUserAction,
            };
            result.RecordActivity(activity, options.MaximumStoredResultActivities);
            if (publishActivityProgress)
            {
                options.ActivityProgress?.Report(activity);
            }
        }

        public static void RecordTransfer(
            SyncRunOptions options,
            SyncTransferDirection direction,
            string relativePath,
            long transferredBytes,
            long? totalBytes,
            bool isCompleted = false)
        {
            options.TransferProgress?.Report(new SyncTransferProgress(
                direction,
                relativePath,
                transferredBytes,
                totalBytes,
                isCompleted));
        }
    }
}
