// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;

namespace Cotton.Sync
{
    internal readonly record struct OnlineOnlyPlaceholderMoveTarget(
        string TargetKey,
        LocalFileSnapshot Local);
}
