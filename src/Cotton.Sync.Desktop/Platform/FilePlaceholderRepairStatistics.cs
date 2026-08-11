// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Platform
{
    internal class FilePlaceholderRepairStatistics
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public int CandidateCount { get; private set; }

        public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

        public int MissingCount { get; private set; }

        public int NonPlaceholderCount { get; private set; }

        public int RepairedCount { get; private set; }

        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public void RecordCandidate()
        {
            CandidateCount++;
        }

        public void RecordMissing()
        {
            MissingCount++;
        }

        public void RecordNonPlaceholder()
        {
            NonPlaceholderCount++;
        }

        public void RecordRepaired()
        {
            RepairedCount++;
        }

        public void Stop()
        {
            _stopwatch.Stop();
        }
    }
}
