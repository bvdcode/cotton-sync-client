// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Describes the sync surface requested by an application coordinator.
    /// </summary>
    public class SyncRunRequest
    {
        private SyncRunRequest(
            bool isFull,
            IReadOnlyList<string> localChangedPaths,
            SyncRunCause causes)
        {
            if (causes == SyncRunCause.None)
            {
                throw new ArgumentOutOfRangeException(nameof(causes), "At least one sync run cause is required.");
            }

            IsFull = isFull;
            LocalChangedPaths = localChangedPaths;
            Causes = causes;
        }

        /// <summary>
        /// Gets a request that reconciles the whole sync pair.
        /// </summary>
        public static SyncRunRequest Full { get; } = ForFull(SyncRunCause.Manual);

        /// <summary>
        /// Gets a value indicating whether the whole sync pair must be reconciled.
        /// </summary>
        public bool IsFull { get; }

        /// <summary>
        /// Gets the events that requested this pass.
        /// </summary>
        public SyncRunCause Causes { get; }

        /// <summary>
        /// Gets local relative paths that should be reconciled.
        /// </summary>
        public IReadOnlyList<string> LocalChangedPaths { get; }

        /// <summary>
        /// Creates a local-path request.
        /// </summary>
        public static SyncRunRequest ForFull(SyncRunCause causes)
        {
            return new SyncRunRequest(true, Array.Empty<string>(), causes);
        }

        /// <summary>
        /// Creates a local-path request.
        /// </summary>
        public static SyncRunRequest ForLocalChangedPaths(
            IEnumerable<string> relativePaths,
            SyncRunCause causes = SyncRunCause.LocalChange)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            List<string> paths = relativePaths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
            {
                throw new ArgumentException("At least one changed path is required for a scoped sync request.", nameof(relativePaths));
            }

            return new SyncRunRequest(false, paths, causes);
        }

        /// <summary>
        /// Merges two requests without losing a required full reconcile.
        /// </summary>
        public SyncRunRequest Merge(SyncRunRequest other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (IsFull || other.IsFull)
            {
                IReadOnlyList<string> mergedPaths = NormalizeLocalChangedPaths(
                    LocalChangedPaths.Concat(other.LocalChangedPaths));
                return new SyncRunRequest(true, mergedPaths, Causes | other.Causes);
            }

            return ForLocalChangedPaths(
                LocalChangedPaths.Concat(other.LocalChangedPaths),
                Causes | other.Causes);
        }

        private static IReadOnlyList<string> NormalizeLocalChangedPaths(IEnumerable<string> relativePaths)
        {
            return relativePaths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
