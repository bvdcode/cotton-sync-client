// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsCloudFilesDirectoryRequestFactory
    {
        public static RemoteDirectoryMaterializationRequest Create(SyncPairSettings pair, SyncStateEntry directory, string relativePath)
        {
            if (directory.Kind != SyncEntryKind.Directory || directory.RemoteNodeId is not Guid nodeId)
            {
                throw new InvalidOperationException("Cloud Files directory setup requires a tracked remote directory.");
            }

            string normalizedPath = SyncPath.Normalize(relativePath);
            return new RemoteDirectoryMaterializationRequest(
                pair.Id.ToString("D"), pair.LocalRootPath, pair.RemoteRootNodeId, normalizedPath,
                new NodeDto
                {
                    Id = nodeId,
                    Name = normalizedPath.Split('/')[^1],
                    CreatedAt = directory.SyncedAtUtc,
                    UpdatedAt = directory.SyncedAtUtc,
                });
        }
    }
}
