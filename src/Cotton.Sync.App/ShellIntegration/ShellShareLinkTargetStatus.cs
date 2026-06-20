// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.ShellIntegration
{
    public enum ShellShareLinkTargetStatus
    {
        Resolved = 0,
        OutsideSyncRoot = 1,
        SyncPairDisabled = 2,
        IgnoredPath = 3,
        MissingBaseline = 4,
        MissingRemoteIdentity = 5,
    }
}
