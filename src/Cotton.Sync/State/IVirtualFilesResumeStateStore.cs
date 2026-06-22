// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.State
{
    /// <summary>
    /// Provides compact resume-state reads for high-scale Windows virtual-files seeding.
    /// </summary>
    public interface IVirtualFilesResumeStateStore
    {
        /// <summary>
        /// Streams only the state columns needed to resume Windows virtual-files seeding.
        /// </summary>
        IAsyncEnumerable<SyncVirtualFilesResumeEntry> LoadVirtualFilesResumeEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            CancellationToken cancellationToken = default);
    }
}
