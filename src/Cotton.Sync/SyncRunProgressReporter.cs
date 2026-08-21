// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal static class SyncRunProgressReporter
    {
        private const int DetailedItemInterval = 25;
        private const int DetailedItemLimit = 50_000;
        private const int SparseItemInterval = 100;
        private static readonly TimeSpan ReportTimeInterval = TimeSpan.FromMilliseconds(250);

        public static void Report(
            SyncRunOptions options,
            SyncRunProgressStage stage,
            int filesCompleted,
            int? filesTotal,
            string? currentPath,
            DateTime startedAtUtc,
            bool isCompleted = false,
            long bytesCompleted = 0,
            long? bytesTotal = null)
        {
            options.RunProgress?.Report(new SyncRunProgress(
                stage,
                filesCompleted,
                filesTotal,
                currentPath,
                startedAtUtc,
                isCompleted,
                bytesCompleted,
                bytesTotal));
        }

        public static void ReportRunProgress(
            SyncRunOptions options,
            SyncRunProgressStage stage,
            int filesCompleted,
            int? filesTotal,
            string? currentPath,
            DateTime startedAtUtc,
            bool isCompleted = false,
            long bytesCompleted = 0,
            long? bytesTotal = null)
        {
            Report(
                options,
                stage,
                filesCompleted,
                filesTotal,
                currentPath,
                startedAtUtc,
                isCompleted,
                bytesCompleted,
                bytesTotal);
        }

        public static void ReportItemRunProgress(
            SyncRunOptions options,
            SyncRunProgressStage stage,
            int itemsCompleted,
            int itemsTotal,
            string? currentPath,
            DateTime startedAtUtc,
            ref DateTime? lastReportedAtUtc,
            long bytesCompleted = 0,
            long? bytesTotal = null)
        {
            DateTime occurredAtUtc = DateTime.UtcNow;
            if (!ShouldReportItemRunProgress(itemsCompleted, itemsTotal, lastReportedAtUtc, occurredAtUtc))
            {
                return;
            }

            lastReportedAtUtc = occurredAtUtc;
            Report(
                options,
                stage,
                itemsCompleted,
                itemsTotal,
                currentPath,
                startedAtUtc,
                bytesCompleted: bytesCompleted,
                bytesTotal: bytesTotal);
        }

        public static async ValueTask YieldAfterLargeBatchAsync(
            SyncRunOptions options,
            int itemsCompleted,
            int itemsTotal,
            CancellationToken cancellationToken)
        {
            int itemInterval = GetReportItemInterval(itemsTotal);
            if (itemsTotal <= itemInterval
                || itemsCompleted <= 0
                || itemsCompleted >= itemsTotal
                || itemsCompleted % itemInterval != 0)
            {
                return;
            }

            if (options.CooperativeYieldAsync is { } cooperativeYieldAsync)
            {
                await cooperativeYieldAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public static bool ShouldReportItemRunProgress(
            int itemsCompleted,
            int itemsTotal,
            DateTime? lastReportedAtUtc,
            DateTime occurredAtUtc)
        {
            int itemInterval = GetReportItemInterval(itemsTotal);
            return itemsTotal <= itemInterval
                || itemsCompleted == 0
                || itemsCompleted == itemsTotal
                || itemsCompleted % itemInterval == 0
                || (lastReportedAtUtc.HasValue
                    && occurredAtUtc - lastReportedAtUtc.Value >= ReportTimeInterval);
        }

        private static int GetReportItemInterval(int itemsTotal)
        {
            return itemsTotal <= DetailedItemLimit ? DetailedItemInterval : SparseItemInterval;
        }
    }
}
