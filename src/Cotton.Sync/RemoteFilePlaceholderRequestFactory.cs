// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync
{
    internal static class RemoteFilePlaceholderRequestFactory
    {
        public static RemoteFilePlaceholderRequest Create(
            SyncPair syncPair,
            string relativePath,
            NodeFileManifestDto remoteFile,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                SyncPath.Normalize(relativePath),
                remoteFile,
                existingHydrationState);
        }
    }
}
