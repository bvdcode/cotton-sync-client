// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    internal enum RemoteNamedPathStatus
    {
        UnknownParent = 0,
        Resolved = 1,
        Ignored = 2,
        Invalid = 3,
    }
}
