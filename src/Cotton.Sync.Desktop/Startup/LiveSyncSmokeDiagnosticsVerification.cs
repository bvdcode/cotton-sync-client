// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal record LiveSyncSmokeDiagnosticsVerification(bool Passed, string Details)
    {
        public static LiveSyncSmokeDiagnosticsVerification Failed(string details)
        {
            return new LiveSyncSmokeDiagnosticsVerification(false, details);
        }
    }
}
