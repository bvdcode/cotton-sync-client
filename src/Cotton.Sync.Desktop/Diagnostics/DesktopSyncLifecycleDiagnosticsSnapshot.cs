// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal sealed record DesktopSyncLifecycleDiagnosticsSnapshot(
        bool IsSignedIn,
        string SyncCoreState,
        bool IsBackgroundActive,
        int SyncPairCount,
        int EnabledSyncPairCount,
        bool HasNoSyncPairs,
        bool IsZeroPairBackgroundActive,
        string Status,
        string Details);
}
