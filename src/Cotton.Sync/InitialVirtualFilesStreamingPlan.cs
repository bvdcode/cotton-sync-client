// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;

namespace Cotton.Sync
{
    internal record InitialVirtualFilesStreamingPlan(
        bool SkipCurrentPlaceholders,
        IReadOnlyDictionary<string, InitialVirtualFilesPlaceholderBaseline> CurrentPlaceholderBaselineByPath,
        IReadOnlyDictionary<string, LocalFileSnapshot> AdoptableUntrackedPlaceholderByPath);
}
