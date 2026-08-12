// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal class SyncFileReconciliationProgress(long plannedTransferBytesTotal)
    {
        private readonly Dictionary<SyncRunProgressStage, int> _completedFilesByStage = [];
        private readonly Dictionary<SyncRunProgressStage, DateTime> _lastReportedAtUtcByStage = [];

        public int FilesCompleted { get; private set; }

        public long CompletedTransferBytes { get; private set; }

        public long PlannedTransferBytesTotal { get; } = plannedTransferBytesTotal;

        public int GetFilesCompleted(SyncRunProgressStage stage)
        {
            return _completedFilesByStage.GetValueOrDefault(stage);
        }

        public DateTime? GetLastReportedAtUtc(SyncRunProgressStage stage)
        {
            return _lastReportedAtUtcByStage.TryGetValue(stage, out DateTime lastReportedAtUtc)
                ? lastReportedAtUtc
                : null;
        }

        public void SetLastReportedAtUtc(SyncRunProgressStage stage, DateTime? lastReportedAtUtc)
        {
            if (lastReportedAtUtc.HasValue)
            {
                _lastReportedAtUtcByStage[stage] = lastReportedAtUtc.Value;
                return;
            }

            _lastReportedAtUtcByStage.Remove(stage);
        }

        public void CompleteFile(SyncRunProgressStage stage, long plannedTransferBytes)
        {
            FilesCompleted++;
            _completedFilesByStage[stage] = GetFilesCompleted(stage) + 1;
            CompletedTransferBytes += plannedTransferBytes;
        }
    }
}
