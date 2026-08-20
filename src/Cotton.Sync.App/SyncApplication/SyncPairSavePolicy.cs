// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.SyncApplication
{
    internal static class SyncPairSavePolicy
    {
        public static SyncPairValidationError? ValidateScopeChange(
            SyncPairSettings? existingSyncPair,
            SyncPairSettings syncPair)
        {
            if (existingSyncPair is null)
            {
                return null;
            }

            bool scopeChanged = !SyncPairSettingsValidator.AreSameLocalRoot(
                    existingSyncPair.LocalRootPath,
                    syncPair.LocalRootPath)
                || existingSyncPair.RemoteRootNodeId != syncPair.RemoteRootNodeId
                || syncPair.Mode != existingSyncPair.Mode;
            return scopeChanged
                ? new SyncPairValidationError(
                    SyncPairValidationIssue.SyncScopeChangeNotSupported,
                    syncPair.Id,
                    null,
                    "To change the local folder, cloud folder, or sync mode, remove this sync folder and add a new one.")
                : null;
        }

        public static bool RequiresPrerequisiteValidation(
            SyncPairSettings? existingSyncPair,
            SyncPairSettings syncPair)
        {
            if (!syncPair.IsEnabled)
            {
                return false;
            }

            return existingSyncPair is null || !existingSyncPair.IsEnabled;
        }
    }
}
