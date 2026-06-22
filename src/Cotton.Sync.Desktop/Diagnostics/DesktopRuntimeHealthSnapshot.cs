// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal sealed record DesktopRuntimeHealthSnapshot(
        int ProcessId,
        string ProcessName,
        long WorkingSetBytes,
        long? PrivateMemoryBytes,
        int? ThreadCount,
        int? HandleCount);
}
