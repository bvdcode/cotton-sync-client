// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync
{
    internal class RemoteTreeScanProgressCounter : IProgress<RemoteTreeScanProgress>
    {
        private int _pagesScanned;
        private int _entriesExpected;
        private long _pageReadLatencyTotalTicks;
        private long _pageReadLatencyMaxTicks;
        private long _lastPageReadLatencyTicks;

        public int PagesScanned => Volatile.Read(ref _pagesScanned);

        public int EntriesExpected => Volatile.Read(ref _entriesExpected);

        public TimeSpan PageReadLatencyTotal => TimeSpan.FromTicks(Volatile.Read(ref _pageReadLatencyTotalTicks));

        public TimeSpan PageReadLatencyMax => TimeSpan.FromTicks(Volatile.Read(ref _pageReadLatencyMaxTicks));

        public TimeSpan LastPageReadLatency => TimeSpan.FromTicks(Volatile.Read(ref _lastPageReadLatencyTicks));

        public void Report(RemoteTreeScanProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            int current;
            do
            {
                current = Volatile.Read(ref _pagesScanned);
                if (value.PagesScanned <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _pagesScanned, value.PagesScanned, current) != current);

            Volatile.Write(ref _pageReadLatencyTotalTicks, value.PageReadLatencyTotal.Ticks);
            Volatile.Write(ref _pageReadLatencyMaxTicks, value.PageReadLatencyMax.Ticks);
            Volatile.Write(ref _lastPageReadLatencyTicks, value.LastPageReadLatency.Ticks);
            if (value.EntriesExpected is { } entriesExpected)
            {
                Volatile.Write(ref _entriesExpected, entriesExpected);
            }
        }
    }
}
