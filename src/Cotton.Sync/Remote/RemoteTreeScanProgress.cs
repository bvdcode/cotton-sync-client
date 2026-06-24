// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Describes progress while scanning a remote Cotton folder tree.
    /// </summary>
    public class RemoteTreeScanProgress
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTreeScanProgress" /> class.
        /// </summary>
        public RemoteTreeScanProgress(
            int filesScanned,
            int directoriesScanned,
            string? currentPath,
            int pagesScanned = 0,
            TimeSpan pageReadLatencyTotal = default,
            TimeSpan pageReadLatencyMax = default,
            TimeSpan lastPageReadLatency = default,
            int? entriesExpected = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(filesScanned);
            ArgumentOutOfRangeException.ThrowIfNegative(directoriesScanned);
            ArgumentOutOfRangeException.ThrowIfNegative(pagesScanned);
            ArgumentOutOfRangeException.ThrowIfNegative(pageReadLatencyTotal.Ticks);
            ArgumentOutOfRangeException.ThrowIfNegative(pageReadLatencyMax.Ticks);
            ArgumentOutOfRangeException.ThrowIfNegative(lastPageReadLatency.Ticks);
            if (entriesExpected.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(entriesExpected.Value);
            }

            FilesScanned = filesScanned;
            DirectoriesScanned = directoriesScanned;
            CurrentPath = string.IsNullOrWhiteSpace(currentPath) ? string.Empty : SyncPath.Normalize(currentPath);
            PagesScanned = pagesScanned;
            PageReadLatencyTotal = pageReadLatencyTotal;
            PageReadLatencyMax = pageReadLatencyMax;
            LastPageReadLatency = lastPageReadLatency;
            EntriesExpected = entriesExpected;
        }

        /// <summary>
        /// Gets the number of remote file entries discovered so far.
        /// </summary>
        public int FilesScanned { get; }

        /// <summary>
        /// Gets the number of remote directory entries discovered so far.
        /// </summary>
        public int DirectoriesScanned { get; }

        /// <summary>
        /// Gets the number of remote child pages loaded so far.
        /// </summary>
        public int PagesScanned { get; }

        /// <summary>
        /// Gets the number of remote file and directory entries expected from already discovered remote pages.
        /// </summary>
        public int? EntriesExpected { get; }

        /// <summary>
        /// Gets the cumulative latency spent reading remote child pages.
        /// </summary>
        public TimeSpan PageReadLatencyTotal { get; }

        /// <summary>
        /// Gets the slowest observed remote child page read latency.
        /// </summary>
        public TimeSpan PageReadLatencyMax { get; }

        /// <summary>
        /// Gets the most recent remote child page read latency.
        /// </summary>
        public TimeSpan LastPageReadLatency { get; }

        /// <summary>
        /// Gets the most recent discovered remote path when available.
        /// </summary>
        public string CurrentPath { get; }
    }
}
