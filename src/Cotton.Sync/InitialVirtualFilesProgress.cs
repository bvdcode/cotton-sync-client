// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal static class InitialVirtualFilesProgress
    {
        public static void Report(InitialVirtualFilesPopulationContext context, string relativePath)
        {
            Report(
                context.Options,
                context.Metrics.CompletedFiles,
                context.Metrics.DiscoveredFiles,
                context.Metrics.CompletedDirectories,
                context.Metrics.DiscoveredDirectories,
                context.Metrics.ExpectedItems,
                relativePath,
                context.StartedAtUtc,
                context.Metrics.LastPlaceholderProgressReportedAtUtc,
                value => context.Metrics.LastPlaceholderProgressReportedAtUtc = value);
        }

        public static bool Report(
            SyncRunOptions options,
            int filesCompleted,
            int filesDiscovered,
            int directoriesCompleted,
            int directoriesDiscovered,
            int expectedItems,
            string relativePath,
            DateTime startedAtUtc,
            DateTime? lastReportedAtUtc,
            Action<DateTime?> setLastReportedAtUtc)
        {
            int itemsCompleted = GetItemCount(filesCompleted, directoriesCompleted);
            int itemsDiscovered = GetItemCount(filesDiscovered, directoriesDiscovered);
            int itemsTotal = Math.Max(itemsCompleted, Math.Max(itemsDiscovered, expectedItems));
            DateTime occurredAtUtc = DateTime.UtcNow;
            if (!SyncRunProgressReporter.ShouldReportItemRunProgress(
                    itemsCompleted,
                    itemsTotal,
                    lastReportedAtUtc,
                    occurredAtUtc))
            {
                return false;
            }

            setLastReportedAtUtc(occurredAtUtc);
            SyncRunProgressReporter.ReportRunProgress(
                options,
                SyncRunProgressStage.CreatingPlaceholders,
                itemsCompleted,
                itemsTotal,
                relativePath,
                startedAtUtc);
            return true;
        }

        public static int GetItemCount(int fileCount, int directoryCount)
        {
            return checked(fileCount + directoryCount);
        }
    }
}
