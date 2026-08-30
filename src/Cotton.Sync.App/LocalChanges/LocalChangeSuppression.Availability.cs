// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using SuppressionEntry = Cotton.Sync.App.LocalChanges.LocalChangeSuppressionEntry;

namespace Cotton.Sync.App.LocalChanges
{
    public partial class LocalChangeSuppression
    {
        private bool HasAvailabilityConditionEnded(
            Guid syncPairId,
            string fullPath,
            SuppressionEntry entry)
        {
            if (_registry.IsActiveBurstPath(syncPairId, fullPath))
            {
                return entry.AvailabilityCondition switch
                {
                    LocalChangeSuppressionAvailabilityCondition.None => false,
                    LocalChangeSuppressionAvailabilityCondition.OnlineOnly
                        => _pinnedCloudFilesPlaceholderProbe(fullPath),
                    LocalChangeSuppressionAvailabilityCondition.Pinned
                        => _unpinnedCloudFilesPlaceholderProbe(fullPath),
                    LocalChangeSuppressionAvailabilityCondition.Unpinned
                        => _pinnedCloudFilesPlaceholderProbe(fullPath),
                    _ => throw CreateAvailabilityConditionException(entry),
                };
            }

            return entry.AvailabilityCondition switch
            {
                LocalChangeSuppressionAvailabilityCondition.None => false,
                LocalChangeSuppressionAvailabilityCondition.OnlineOnly
                    => !_onlineOnlyCloudFilesPlaceholderProbe(fullPath),
                LocalChangeSuppressionAvailabilityCondition.Pinned
                    => !_pinnedCloudFilesPlaceholderProbe(fullPath),
                LocalChangeSuppressionAvailabilityCondition.Unpinned
                    => !_unpinnedCloudFilesPlaceholderProbe(fullPath),
                _ => throw CreateAvailabilityConditionException(entry),
            };
        }

        private static ArgumentOutOfRangeException CreateAvailabilityConditionException(SuppressionEntry entry)
        {
            return new ArgumentOutOfRangeException(
                nameof(entry),
                entry.AvailabilityCondition,
                "Unsupported local change suppression availability condition.");
        }
    }
}
