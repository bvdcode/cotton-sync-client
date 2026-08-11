// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync
{
    internal readonly record struct OnlineOnlyPlaceholderMoveMatch(
        string SourceKey,
        SyncStateEntry SourceState,
        RemoteFileSnapshot Remote,
        string TargetKey,
        LocalFileSnapshot Local);
}
