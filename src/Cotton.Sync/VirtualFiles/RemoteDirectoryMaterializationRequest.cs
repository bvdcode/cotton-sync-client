// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Describes a remote directory that is about to be represented in the local sync root.
    /// </summary>
    public sealed record RemoteDirectoryMaterializationRequest(
        string SyncPairId,
        string LocalRootPath,
        Guid RemoteRootNodeId,
        string RelativePath,
        NodeDto RemoteDirectory);
}
