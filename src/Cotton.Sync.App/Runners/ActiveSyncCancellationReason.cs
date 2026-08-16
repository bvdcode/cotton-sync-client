// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    internal enum ActiveSyncCancellationReason
    {
        None = 0,
        Pause = 1,
        Stop = 2,
        Superseded = 3,
    }
}
