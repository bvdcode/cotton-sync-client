// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;

namespace Cotton.Sync
{
    internal readonly record struct RemoteDirectoryCreationResult(NodeDto Node, bool ReusedExisting);
}
