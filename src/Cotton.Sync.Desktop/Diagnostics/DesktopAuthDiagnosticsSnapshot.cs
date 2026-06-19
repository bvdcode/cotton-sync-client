// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal sealed record DesktopAuthDiagnosticsSnapshot(
        DateTimeOffset? LastUnauthorizedChallengeAtUtc,
        string LastTokenRefreshStatus,
        DateTimeOffset? LastTokenRefreshAtUtc,
        int TokenSaveCount,
        int TokenRefreshSaveCount,
        string LastSessionRestoreStatus,
        DateTimeOffset? LastSessionRestoreAtUtc,
        int LastSessionRestoreAttempts,
        string? LastSessionRestoreFailureType,
        string? LastSessionRestoreFailureMessage,
        DateTimeOffset? LastSessionRevokedAtUtc)
    {
        public static DesktopAuthDiagnosticsSnapshot Initial { get; } = new(
            LastUnauthorizedChallengeAtUtc: null,
            LastTokenRefreshStatus: "notObserved",
            LastTokenRefreshAtUtc: null,
            TokenSaveCount: 0,
            TokenRefreshSaveCount: 0,
            LastSessionRestoreStatus: "notChecked",
            LastSessionRestoreAtUtc: null,
            LastSessionRestoreAttempts: 0,
            LastSessionRestoreFailureType: null,
            LastSessionRestoreFailureMessage: null,
            LastSessionRevokedAtUtc: null);
    }
}
