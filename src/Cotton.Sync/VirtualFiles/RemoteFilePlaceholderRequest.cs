// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Describes a remote-only file that should be exposed locally as a virtual-files placeholder.
    /// </summary>
    public sealed record RemoteFilePlaceholderRequest(
        string SyncPairId,
        string LocalRootPath,
        Guid RemoteRootNodeId,
        string RelativePath,
        NodeFileManifestDto RemoteFile);
}
