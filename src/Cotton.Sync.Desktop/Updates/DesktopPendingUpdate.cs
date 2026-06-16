// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed record DesktopPendingUpdate(
        string Version,
        string InstallerPath,
        string Sha256,
        long SizeBytes,
        DateTime CreatedAtUtc,
        int AttemptCount = 0);
}
