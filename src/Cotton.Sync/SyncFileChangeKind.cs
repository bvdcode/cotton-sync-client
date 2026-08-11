// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal enum SyncFileChangeKind
    {
        None,
        DeleteState,
        DeleteLocal,
        DeleteRemote,
        Upload,
        Download,
        Conflict
    }
}
