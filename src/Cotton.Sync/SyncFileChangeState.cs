// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal readonly record struct SyncFileChangeState(
        bool LocalDeleted,
        bool RemoteDeleted,
        bool LocalChanged,
        bool RemoteChanged,
        bool BaselineDiverged)
    {
        public bool HasChanges => LocalDeleted || RemoteDeleted || LocalChanged || RemoteChanged;
    }
}
