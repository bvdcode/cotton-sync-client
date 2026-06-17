// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesProcessInfo(
        uint ProcessId,
        string? ImagePath,
        string? PackageName,
        string? ApplicationId,
        string? CommandLine,
        uint SessionId);
}
