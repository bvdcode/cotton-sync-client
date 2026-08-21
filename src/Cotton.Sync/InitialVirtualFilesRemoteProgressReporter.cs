// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesRemoteProgressReporter(
        IProgress<RemoteTreeScanProgress> inner,
        SyncRunOptions options,
        DateTime startedAtUtc,
        bool publishRunProgress,
        InitialVirtualFilesPopulationMetrics metrics) : IProgress<RemoteTreeScanProgress>
    {
        public void Report(RemoteTreeScanProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            inner.Report(value);
            if (!publishRunProgress)
            {
                return;
            }

            int itemsDiscovered = InitialVirtualFilesProgress.GetItemCount(
                value.FilesScanned,
                value.DirectoriesScanned);
            if (itemsDiscovered == 0)
            {
                return;
            }

            int itemsCompleted = InitialVirtualFilesProgress.GetItemCount(
                metrics.CompletedFiles,
                metrics.CompletedDirectories);
            int knownItemsTotal = value.EntriesExpected.GetValueOrDefault(itemsDiscovered);
            int itemsTotal = Math.Max(itemsCompleted, Math.Max(itemsDiscovered, knownItemsTotal));
            SyncRunProgressReporter.ReportRunProgress(
                options,
                SyncRunProgressStage.CreatingPlaceholders,
                itemsCompleted,
                itemsTotal,
                value.CurrentPath,
                startedAtUtc);
        }
    }
}
