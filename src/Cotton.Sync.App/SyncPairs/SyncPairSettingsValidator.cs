// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.App.SyncPairs
{
    /// <summary>
    /// Validates sync-pair settings before they are persisted or used by the sync supervisor.
    /// </summary>
    public class SyncPairSettingsValidator
    {
        private readonly SyncPairModeCapabilitySnapshot _modeCapabilities;

        public SyncPairSettingsValidator(SyncPairModeCapabilitySnapshot? modeCapabilities = null)
        {
            _modeCapabilities = modeCapabilities ?? SyncPairModeCapabilitySnapshot.FullMirrorOnly;
        }

        /// <summary>
        /// Validates a set of sync-pair settings.
        /// </summary>
        public SyncPairValidationResult Validate(IReadOnlyCollection<SyncPairSettings> syncPairs)
        {
            ArgumentNullException.ThrowIfNull(syncPairs);
            List<SyncPairValidationError> errors = new List<SyncPairValidationError>();
            foreach (SyncPairSettings syncPair in syncPairs)
            {
                ValidateSingle(syncPair, errors);
            }

            ValidateLocalRootOverlaps(syncPairs, errors);
            return new SyncPairValidationResult(errors);
        }

        private void ValidateSingle(SyncPairSettings syncPair, ICollection<SyncPairValidationError> errors)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            if (syncPair.Id == Guid.Empty)
            {
                Add(errors, SyncPairValidationIssue.EmptyId, syncPair.Id, null, "Sync pair id is required.");
            }

            if (string.IsNullOrWhiteSpace(syncPair.DisplayName))
            {
                Add(errors, SyncPairValidationIssue.EmptyDisplayName, syncPair.Id, null, "Sync pair display name is required.");
            }

            if (string.IsNullOrWhiteSpace(syncPair.LocalRootPath))
            {
                Add(errors, SyncPairValidationIssue.EmptyLocalRootPath, syncPair.Id, null, "Local root path is required.");
            }

            if (syncPair.RemoteRootNodeId == Guid.Empty)
            {
                Add(errors, SyncPairValidationIssue.EmptyRemoteRootNodeId, syncPair.Id, null, "Remote root node id is required.");
            }

            if (string.IsNullOrWhiteSpace(syncPair.RemoteDisplayPath))
            {
                Add(errors, SyncPairValidationIssue.EmptyRemoteDisplayPath, syncPair.Id, null, "Remote display path is required.");
            }

            if (!_modeCapabilities.IsSupported(syncPair.Mode))
            {
                Add(
                    errors,
                    SyncPairValidationIssue.UnsupportedMode,
                    syncPair.Id,
                    null,
                    _modeCapabilities.GetUnsupportedMessage(syncPair.Mode));
            }
        }

        private static void ValidateLocalRootOverlaps(
            IReadOnlyCollection<SyncPairSettings> syncPairs,
            ICollection<SyncPairValidationError> errors)
        {
            List<NormalizedLocalRoot> roots = new List<NormalizedLocalRoot>();
            foreach (SyncPairSettings syncPair in syncPairs)
            {
                if (string.IsNullOrWhiteSpace(syncPair.LocalRootPath))
                {
                    continue;
                }

                NormalizedPath? path = NormalizeLocalRoot(syncPair.LocalRootPath);
                if (path is null)
                {
                    Add(errors, SyncPairValidationIssue.LocalRootUnavailable, syncPair.Id, null,
                        "The local sync root does not exist and cannot be created or accessed.");
                    continue;
                }

                roots.Add(new NormalizedLocalRoot(syncPair.Id, path));
            }

            for (int leftIndex = 0; leftIndex < roots.Count; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < roots.Count; rightIndex++)
                {
                    NormalizedLocalRoot left = roots[leftIndex];
                    NormalizedLocalRoot right = roots[rightIndex];
                    if (!left.Path.IsSameStyle(right.Path) || !left.Path.Overlaps(right.Path))
                    {
                        continue;
                    }

                    Add(
                        errors,
                        SyncPairValidationIssue.OverlappingLocalRoots,
                        left.SyncPairId,
                        right.SyncPairId,
                        left.Path.IsSamePath(right.Path)
                            ? "This folder is already syncing."
                            : "Sync folders cannot be inside each other.");
                }
            }
        }

        private static NormalizedPath? NormalizeLocalRoot(string localRootPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(localRootPath.Trim());
                return new NormalizedPath(Path.TrimEndingDirectorySeparator(fullPath), OperatingSystem.IsWindows());
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Trace.TraceWarning("Cannot normalize a local sync root: {0}", exception.Message);
                return null;
            }
        }

        internal static bool AreSameLocalRoot(string left, string right)
        {
            NormalizedPath? normalizedLeft = NormalizeLocalRoot(left);
            NormalizedPath? normalizedRight = NormalizeLocalRoot(right);
            return normalizedLeft is not null && normalizedRight is not null
                && normalizedLeft.IsSameStyle(normalizedRight)
                && normalizedLeft.IsSamePath(normalizedRight);
        }

        private static void Add(
            ICollection<SyncPairValidationError> errors,
            SyncPairValidationIssue issue,
            Guid? syncPairId,
            Guid? otherSyncPairId,
            string message)
        {
            errors.Add(new SyncPairValidationError(issue, syncPairId, otherSyncPairId, message));
        }

    }
}
