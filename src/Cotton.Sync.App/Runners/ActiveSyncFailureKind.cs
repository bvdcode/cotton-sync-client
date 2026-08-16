// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    internal enum ActiveSyncFailureKind
    {
        PausedCancellation = 0,
        Canceled = 1,
        PausedSideEffect = 2,
        Stopped = 3,
        Failed = 4,
        Superseded = 5,
    }
}
