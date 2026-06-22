// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal static class DesktopAuthDiagnosticsState
    {
        private static readonly object Gate = new();
        private static DesktopAuthDiagnosticsSnapshot _snapshot = DesktopAuthDiagnosticsSnapshot.Initial;
        private static int _pendingUnauthorizedChallenges;

        public static DesktopAuthDiagnosticsSnapshot Snapshot()
        {
            lock (Gate)
            {
                return _snapshot;
            }
        }

        public static void RecordUnauthorizedChallenge()
        {
            lock (Gate)
            {
                _pendingUnauthorizedChallenges++;
                _snapshot = _snapshot with
                {
                    LastUnauthorizedChallengeAtUtc = DateTimeOffset.UtcNow,
                    LastTokenRefreshStatus = _snapshot.LastTokenRefreshStatus == "succeeded"
                        ? _snapshot.LastTokenRefreshStatus
                        : "challengeObserved",
                };
            }
        }

        public static void RecordTokenSave(bool replacedExistingTokens)
        {
            lock (Gate)
            {
                string refreshStatus = _snapshot.LastTokenRefreshStatus;
                DateTimeOffset? refreshAtUtc = _snapshot.LastTokenRefreshAtUtc;
                int refreshSaveCount = _snapshot.TokenRefreshSaveCount;
                if (replacedExistingTokens)
                {
                    refreshSaveCount++;
                    refreshAtUtc = DateTimeOffset.UtcNow;
                    refreshStatus = _pendingUnauthorizedChallenges > 0 ? "succeeded" : "tokenUpdated";
                    _pendingUnauthorizedChallenges = 0;
                }

                _snapshot = _snapshot with
                {
                    LastTokenRefreshStatus = refreshStatus,
                    LastTokenRefreshAtUtc = refreshAtUtc,
                    TokenSaveCount = _snapshot.TokenSaveCount + 1,
                    TokenRefreshSaveCount = refreshSaveCount,
                };
            }
        }

        public static void RecordSessionRestoreSkipped(string status)
        {
            RecordSessionRestore(status, attempts: 0, exception: null);
        }

        public static void RecordSessionRestoreSucceeded(int attempts)
        {
            RecordSessionRestore("succeeded", attempts, exception: null);
        }

        public static void RecordSessionRestoreRejected(int attempts, Exception exception)
        {
            RecordSessionRestore("rejected", attempts, exception);
        }

        public static void RecordSessionRestoreFailed(string status, int attempts, Exception exception)
        {
            RecordSessionRestore(status, attempts, exception);
        }

        public static void RecordSessionRevoked(DateTimeOffset occurredAtUtc)
        {
            lock (Gate)
            {
                _snapshot = _snapshot with { LastSessionRevokedAtUtc = occurredAtUtc };
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _snapshot = DesktopAuthDiagnosticsSnapshot.Initial;
                _pendingUnauthorizedChallenges = 0;
            }
        }

        private static void RecordSessionRestore(string status, int attempts, Exception? exception)
        {
            lock (Gate)
            {
                _snapshot = _snapshot with
                {
                    LastSessionRestoreStatus = status,
                    LastSessionRestoreAtUtc = DateTimeOffset.UtcNow,
                    LastSessionRestoreAttempts = attempts,
                    LastSessionRestoreFailureType = exception?.GetType().Name,
                    LastSessionRestoreFailureMessage = exception is null
                        ? null
                        : DesktopSecretRedactor.Redact(exception.Message),
                };
            }
        }
    }
}
