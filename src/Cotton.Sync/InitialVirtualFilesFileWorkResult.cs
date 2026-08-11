// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal record InitialVirtualFilesFileWorkResult(
        string RelativePath,
        SyncStateEntry? State,
        SyncActivityKind ActivityKind,
        string? Details,
        bool RequiresUserAction,
        bool ReportActivity);
}
