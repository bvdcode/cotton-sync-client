// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Local
{
    /// <summary>
    /// Scans selected local paths into metadata lookups without walking unrelated folders.
    /// </summary>
    public interface ILocalFileMetadataPathLookupScanner
    {
        /// <summary>
        /// Scans selected relative paths, their ancestors, and optionally local descendants under directory target paths.
        /// </summary>
        Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
            string rootPath,
            IReadOnlyCollection<string> relativePaths,
            IProgress<LocalTreeScanProgress>? progress,
            bool includeDirectoryDescendants,
            CancellationToken cancellationToken = default);
    }
}
