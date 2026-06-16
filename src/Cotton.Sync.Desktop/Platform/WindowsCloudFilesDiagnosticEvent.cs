// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesDiagnosticEvent(
        DateTimeOffset TimestampUtc,
        string Operation,
        string Status,
        string? SyncPairId = null,
        string? LocalRootPath = null,
        string? RelativePath = null,
        string? Details = null,
        int? HResult = null);
}
