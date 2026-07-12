// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync
{
    /// <summary>
    /// Defines the filesystem surface that a sync pass must reconcile.
    /// </summary>
    public sealed class SyncRunScope
    {
        private SyncRunScope(
            bool isFull,
            IReadOnlyList<string> localChangedPaths,
            IReadOnlyList<string> localDeletedPaths)
        {
            IsFull = isFull;
            LocalChangedPaths = localChangedPaths;
            LocalDeletedPaths = localDeletedPaths;
        }

        /// <summary>
        /// Gets a scope that reconciles the whole sync pair.
        /// </summary>
        public static SyncRunScope Full { get; } = new(true, Array.Empty<string>(), Array.Empty<string>());

        /// <summary>
        /// Gets a value indicating whether the run must reconcile the whole sync pair.
        /// </summary>
        public bool IsFull { get; }

        /// <summary>
        /// Gets normalized local relative paths that triggered this pass.
        /// </summary>
        public IReadOnlyList<string> LocalChangedPaths { get; }

        /// <summary>
        /// Gets normalized local relative paths that were deleted by a local filesystem event.
        /// </summary>
        public IReadOnlyList<string> LocalDeletedPaths { get; }

        /// <summary>
        /// Creates a scope for local changed paths.
        /// </summary>
        public static SyncRunScope ForLocalChangedPaths(IEnumerable<string> relativePaths)
        {
            return ForLocalChangedPaths(relativePaths, Array.Empty<string>());
        }

        /// <summary>
        /// Creates a scope for local changed paths.
        /// </summary>
        public static SyncRunScope ForLocalChangedPaths(
            IEnumerable<string> relativePaths,
            IEnumerable<string> localDeletedPaths)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            ArgumentNullException.ThrowIfNull(localDeletedPaths);
            List<string> deletedPaths = NormalizePaths(localDeletedPaths);
            List<string> paths = NormalizePaths(relativePaths.Concat(deletedPaths));
            if (paths.Count == 0)
            {
                throw new ArgumentException("At least one changed path is required for a scoped sync run.", nameof(relativePaths));
            }

            return new SyncRunScope(false, paths, deletedPaths);
        }

        private static List<string> NormalizePaths(IEnumerable<string> relativePaths)
        {
            return relativePaths
                .Select(SyncPath.Normalize)
                .Where(static path => !string.IsNullOrWhiteSpace(path) && !SyncPathIgnoreRules.ShouldIgnore(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
