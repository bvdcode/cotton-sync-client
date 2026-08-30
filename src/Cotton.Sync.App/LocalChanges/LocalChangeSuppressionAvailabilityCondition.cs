// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.LocalChanges
{
    internal enum LocalChangeSuppressionAvailabilityCondition
    {
        None = 0,
        OnlineOnly = 1,
        Pinned = 2,
        Unpinned = 3,
    }
}
