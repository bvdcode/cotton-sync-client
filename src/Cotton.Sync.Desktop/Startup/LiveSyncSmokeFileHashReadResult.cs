// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal record LiveSyncSmokeFileHashReadResult(string Path, string? Sha256, string? Error);
}
