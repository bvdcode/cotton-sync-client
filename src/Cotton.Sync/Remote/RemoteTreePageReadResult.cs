// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;

namespace Cotton.Sync.Remote
{
    internal record struct RemoteTreePageReadResult(
        NodeContentDto Children,
        int TotalCount,
        TimeSpan Elapsed);
}
