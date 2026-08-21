// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync
{
    internal static class SyncBaselineFactory
    {
        public static SyncStateEntry BuildBaseline(
            SyncPair syncPair,
            string relativePath,
            string? localContentHash,
            DateTime? localLastWriteUtc,
            long? localSizeBytes,
            NodeFileManifestDto? remoteFile)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.File,
                LocalContentHash = localContentHash,
                LocalLastWriteUtc = localLastWriteUtc?.ToUniversalTime(),
                LocalSizeBytes = localSizeBytes,
                RemoteSizeBytes = remoteFile?.SizeBytes,
                RemoteFileId = remoteFile?.Id,
                RemoteNodeId = remoteFile?.NodeId,
                RemoteFileManifestId = remoteFile?.FileManifestId,
                RemoteOriginalNodeFileId = remoteFile?.OriginalNodeFileId,
                RemoteContentHash = remoteFile?.ContentHash,
                RemoteETag = remoteFile?.ETag,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        public static SyncStateEntry BuildHydratedPlaceholderBaseline(
            SyncPair syncPair,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            SyncStateEntry existingState)
        {
            SyncStateEntry baseline = BuildBaseline(
                syncPair,
                relativePath,
                local.ContentHash,
                local.LastWriteUtc,
                local.SizeBytes,
                remoteFile);
            baseline.PlaceholderIdentity = existingState.PlaceholderIdentity;
            baseline.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            return baseline;
        }

        public static SyncStateEntry BuildPlaceholderBaseline(
            SyncPair syncPair,
            string relativePath,
            NodeFileManifestDto remoteFile,
            RemoteFilePlaceholderResult placeholder,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            SyncPlaceholderHydrationState hydrationState = placeholder.HydrationState == SyncPlaceholderHydrationState.None
                ? SyncPlaceholderHydrationState.RemoteOnly
                : placeholder.HydrationState;
            if (existingHydrationState == SyncPlaceholderHydrationState.Dehydrated
                && hydrationState == SyncPlaceholderHydrationState.RemoteOnly)
            {
                hydrationState = SyncPlaceholderHydrationState.Dehydrated;
            }

            bool materialized = hydrationState == SyncPlaceholderHydrationState.Hydrated;

            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.File,
                LocalContentHash = materialized ? remoteFile.ContentHash : null,
                LocalLastWriteUtc = materialized
                    ? placeholder.LocalLastWriteUtc?.ToUniversalTime() ?? remoteFile.UpdatedAt.ToUniversalTime()
                    : null,
                LocalSizeBytes = materialized ? placeholder.LocalSizeBytes ?? remoteFile.SizeBytes : null,
                RemoteSizeBytes = remoteFile.SizeBytes,
                RemoteFileId = remoteFile.Id,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileManifestId = remoteFile.FileManifestId,
                RemoteOriginalNodeFileId = remoteFile.OriginalNodeFileId,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                PlaceholderIdentity = placeholder.PlaceholderIdentity,
                PlaceholderHydrationState = hydrationState,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        public static SyncStateEntry BuildDirectoryBaseline(
            SyncPair syncPair,
            string relativePath,
            NodeDto remoteNode)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = remoteNode.Id,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        public static string ResolveUploadedLocalContentHash(LocalFileSnapshot local, NodeFileManifestDto uploaded)
        {
            if (!string.IsNullOrWhiteSpace(local.ContentHash))
            {
                return local.ContentHash;
            }

            if (!string.IsNullOrWhiteSpace(uploaded.ContentHash))
            {
                return uploaded.ContentHash;
            }

            throw new InvalidOperationException("Uploaded file manifest does not include a content hash.");
        }
    }
}
