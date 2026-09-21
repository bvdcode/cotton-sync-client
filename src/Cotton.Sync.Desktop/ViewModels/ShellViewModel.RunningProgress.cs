// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private static bool IsRunningSyncPair(SyncPairRowViewModel row)
        {
            return row.IsEnabled && row.Status is "Syncing" or "Scanning";
        }

        private string CreateRunningSyncTitle()
        {
            SyncPairRowViewModel? row = SyncPairs.FirstOrDefault(IsRunningSyncPair);
            return row is null ? string.Empty : CreateActivePairProgressText(row);
        }

        private static void RestoreRunningSyncPairProgress(SyncPairRowViewModel row)
        {
            if (!IsRunningSyncPair(row))
            {
                ClearSyncPairProgress(row);
                return;
            }

            row.CurrentOperation = row.Status;
            row.HasCurrentProgress = true;
            row.IsCurrentProgressIndeterminate = true;
            row.CurrentProgressValue = 0;
        }
    }
}
