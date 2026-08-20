// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    internal static class RemoteTreeProgressReporter
    {
        private const int ItemInterval = 100;

        public static void ReportFile(
            IProgress<RemoteTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            RemoteTreePageReadMetrics pageMetrics,
            string currentPath,
            int? entriesExpected = null)
        {
            if (progress is not null && (filesScanned == 1 || filesScanned % ItemInterval == 0))
            {
                Report(progress, filesScanned, directoriesScanned, pageMetrics, currentPath, entriesExpected);
            }
        }

        public static void ReportDirectory(
            IProgress<RemoteTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            RemoteTreePageReadMetrics pageMetrics,
            string currentPath,
            int? entriesExpected = null)
        {
            if (progress is not null && (directoriesScanned == 1 || directoriesScanned % ItemInterval == 0))
            {
                Report(progress, filesScanned, directoriesScanned, pageMetrics, currentPath, entriesExpected);
            }
        }

        private static void Report(
            IProgress<RemoteTreeScanProgress> progress,
            int filesScanned,
            int directoriesScanned,
            RemoteTreePageReadMetrics pageMetrics,
            string currentPath,
            int? entriesExpected)
        {
            progress.Report(new RemoteTreeScanProgress(
                filesScanned,
                directoriesScanned,
                currentPath,
                pageMetrics.PagesScanned,
                pageMetrics.PageReadLatencyTotal,
                pageMetrics.PageReadLatencyMax,
                pageMetrics.LastPageReadLatency,
                entriesExpected: entriesExpected));
        }
    }
}
