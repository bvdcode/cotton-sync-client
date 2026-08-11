// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal class InitialVirtualFilesPopulationMetrics(long startingManagedHeapBytes)
    {
        private int _discoveredFiles;
        private int _discoveredDirectories;
        private int _completedFiles;
        private int _completedDirectories;
        private int _createdPlaceholders;
        private int _skippedCurrentPlaceholders;
        private int _skippedUnavailablePlaceholders;
        private int _stateFileRowsWritten;
        private int _stateFileWriteBatches;
        private int _stateDirectoryRowsWritten;
        private long _peakManagedHeapBytes = startingManagedHeapBytes;

        public long StartingManagedHeapBytes { get; } = startingManagedHeapBytes;

        public int DiscoveredFiles => Volatile.Read(ref _discoveredFiles);

        public int DiscoveredDirectories => Volatile.Read(ref _discoveredDirectories);

        public int CompletedFiles => Volatile.Read(ref _completedFiles);

        public int CompletedDirectories => Volatile.Read(ref _completedDirectories);

        public int CreatedPlaceholders => Volatile.Read(ref _createdPlaceholders);

        public int SkippedCurrentPlaceholders => Volatile.Read(ref _skippedCurrentPlaceholders);

        public int SkippedUnavailablePlaceholders => Volatile.Read(ref _skippedUnavailablePlaceholders);

        public int StateFileRowsWritten => Volatile.Read(ref _stateFileRowsWritten);

        public int StateFileWriteBatches => Volatile.Read(ref _stateFileWriteBatches);

        public int StateDirectoryRowsWritten => Volatile.Read(ref _stateDirectoryRowsWritten);

        public long PeakManagedHeapBytes => Volatile.Read(ref _peakManagedHeapBytes);

        public DateTime? LastPlaceholderProgressReportedAtUtc { get; set; }

        public RemoteTreeScanProgressCounter RemoteScanProgress { get; } = new();

        public int ExpectedItems => RemoteScanProgress.EntriesExpected;

        public void RecordDiscoveredFile() => Interlocked.Increment(ref _discoveredFiles);

        public void RecordDiscoveredDirectory() => Interlocked.Increment(ref _discoveredDirectories);

        public int RecordCompletedFile() => Interlocked.Increment(ref _completedFiles);

        public int RecordCompletedDirectory() => Interlocked.Increment(ref _completedDirectories);

        public void RecordFileWorkResult(InitialVirtualFilesFileWorkResult workResult)
        {
            if (workResult.ActivityKind == SyncActivityKind.PlaceholderCreated && workResult.State is not null)
            {
                Interlocked.Increment(ref _createdPlaceholders);
                return;
            }

            if (workResult.ActivityKind != SyncActivityKind.Skipped)
            {
                return;
            }

            if (workResult.ReportActivity)
            {
                Interlocked.Increment(ref _skippedUnavailablePlaceholders);
            }
            else
            {
                Interlocked.Increment(ref _skippedCurrentPlaceholders);
            }
        }

        public void RecordFileStateWrite(int writtenRows)
        {
            if (writtenRows <= 0)
            {
                return;
            }

            Interlocked.Add(ref _stateFileRowsWritten, writtenRows);
            Interlocked.Increment(ref _stateFileWriteBatches);
        }

        public void RecordDirectoryStateWrite(int writtenRows)
        {
            if (writtenRows > 0)
            {
                Interlocked.Add(ref _stateDirectoryRowsWritten, writtenRows);
            }
        }

        public void RecordManagedHeapSample(long value)
        {
            long current;
            do
            {
                current = Volatile.Read(ref _peakManagedHeapBytes);
                if (value <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _peakManagedHeapBytes, value, current) != current);
        }
    }
}
