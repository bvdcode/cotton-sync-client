// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal class SyncFileReconciliationProgress(long plannedTransferBytesTotal)
    {
        public int FilesCompleted { get; private set; }

        public long CompletedTransferBytes { get; private set; }

        public long PlannedTransferBytesTotal { get; } = plannedTransferBytesTotal;

        public DateTime? LastReportedAtUtc { get; set; }

        public void CompleteFile(long plannedTransferBytes)
        {
            FilesCompleted++;
            CompletedTransferBytes += plannedTransferBytes;
        }
    }
}
