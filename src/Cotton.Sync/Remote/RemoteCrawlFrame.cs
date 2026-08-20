// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;

namespace Cotton.Sync.Remote
{
    internal record struct RemoteCrawlFrame(NodeDto Node, string ParentPath, int Page, int Loaded);
}
