// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Describes remote content that is about to be written to a local file.
    /// </summary>
    public record RemoteFileMaterializationRequest(
        string SyncPairId,
        string LocalRootPath,
        Guid RemoteRootNodeId,
        string RelativePath,
        NodeFileManifestDto RemoteFile);
}
