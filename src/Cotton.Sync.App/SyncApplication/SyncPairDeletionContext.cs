// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.SyncApplication
{
    internal class SyncPairDeletionContext
    {
        public bool KeepSyncCoreStoppedUntilPairAdded { get; set; }

        public bool RestartAttempted { get; set; }

        public bool SyncCoreWasRunning { get; set; }

        public bool SyncPairSettingsDeleted { get; set; }
    }
}
