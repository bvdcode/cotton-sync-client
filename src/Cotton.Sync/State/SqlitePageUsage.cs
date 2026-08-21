// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.State
{
    internal readonly record struct SqlitePageUsage(long PageCount, long FreelistCount, long PageSize);
}
