// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Describes the sync surface requested by an application coordinator.
    /// </summary>
    public class SyncRunRequest
    {
        /// <summary>
        /// Gets the maximum number of paths retained by a queued scoped request.
        /// </summary>
        public const int MaximumQueuedScopedPaths = 4_096;

        private SyncRunRequest(
            bool isFull,
            IReadOnlyList<string> localChangedPaths,
            IReadOnlyList<string> localDeletedPaths,
            SyncRunCause causes,
            RemoteDeletePlanApproval? approvedRemoteDeletePlan)
        {
            if (causes == SyncRunCause.None)
            {
                throw new ArgumentOutOfRangeException(nameof(causes), "At least one sync run cause is required.");
            }

            IsFull = isFull;
            LocalChangedPaths = localChangedPaths;
            LocalDeletedPaths = localDeletedPaths;
            Causes = causes;
            ApprovedRemoteDeletePlan = approvedRemoteDeletePlan;
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
        /// Gets the exact remote delete plan explicitly approved for this pass.
        /// </summary>
        public RemoteDeletePlanApproval? ApprovedRemoteDeletePlan { get; }

        /// <summary>
        /// Gets local relative paths that should be reconciled.
        /// </summary>
        public IReadOnlyList<string> LocalChangedPaths { get; }

        /// <summary>
        /// Gets local relative paths that were reported as deleted by the filesystem watcher.
        /// </summary>
        public IReadOnlyList<string> LocalDeletedPaths { get; }

        /// <summary>
        /// Creates a local-path request.
        /// </summary>
        public static SyncRunRequest ForFull(
            SyncRunCause causes,
            RemoteDeletePlanApproval? approvedRemoteDeletePlan = null)
        {
            return new SyncRunRequest(
                true,
                Array.Empty<string>(),
                Array.Empty<string>(),
                causes,
                approvedRemoteDeletePlan);
        }

        /// <summary>
        /// Creates a local-path request.
        /// </summary>
        public static SyncRunRequest ForLocalChangedPaths(
            IEnumerable<string> relativePaths,
            SyncRunCause causes = SyncRunCause.LocalChange)
        {
            return ForLocalChangedPaths(relativePaths, Array.Empty<string>(), causes);
        }

        /// <summary>
        /// Creates a local-path request.
        /// </summary>
        public static SyncRunRequest ForLocalChangedPaths(
            IEnumerable<string> relativePaths,
            IEnumerable<string> localDeletedPaths,
            SyncRunCause causes = SyncRunCause.LocalChange)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            ArgumentNullException.ThrowIfNull(localDeletedPaths);
            IReadOnlyList<string> deletedPaths = NormalizeLocalChangedPaths(localDeletedPaths);
            IReadOnlyList<string> paths = NormalizeLocalChangedPaths(relativePaths.Concat(deletedPaths));
            if (paths.Count == 0)
            {
                throw new ArgumentException("At least one changed path is required for a scoped sync request.", nameof(relativePaths));
            }

            return new SyncRunRequest(false, paths, deletedPaths, causes, approvedRemoteDeletePlan: null);
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
                IReadOnlyList<string> mergedDeletedPaths = NormalizeLocalChangedPaths(
                    LocalDeletedPaths.Concat(other.LocalDeletedPaths));
                RemoteDeletePlanApproval? mergedApprovedRemoteDeletePlan = Equals(
                    ApprovedRemoteDeletePlan,
                    other.ApprovedRemoteDeletePlan)
                    ? ApprovedRemoteDeletePlan
                    : null;
                return new SyncRunRequest(
                    true,
                    mergedPaths,
                    mergedDeletedPaths,
                    Causes | other.Causes,
                    mergedApprovedRemoteDeletePlan);
            }

            return ForLocalChangedPaths(
                LocalChangedPaths.Concat(other.LocalChangedPaths),
                LocalDeletedPaths.Concat(other.LocalDeletedPaths),
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
