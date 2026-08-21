// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text.Json;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsCloudFilesPlaceholderFactory
    {
        public static RemoteFilePlaceholderRequest CreateMissingFilePlaceholderRequest(
            SyncPairSettings syncPair,
            SyncStateEntry fileState)
        {
            byte[] placeholderIdentity = ValidateMissingFilePlaceholderArguments(syncPair, fileState);
            string normalizedPath = SyncPath.Normalize(fileState.RelativePath);
            WindowsCloudFilesPlaceholderIdentity identity =
                WindowsCloudFilesPlaceholderIdentity.Parse(placeholderIdentity);
            ValidateMissingFilePlaceholderIdentity(syncPair, normalizedPath, identity);
            return new RemoteFilePlaceholderRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                normalizedPath,
                new NodeFileManifestDto
                {
                    Id = identity.NodeFileId,
                    NodeId = identity.NodeId,
                    FileManifestId = identity.FileManifestId,
                    OriginalNodeFileId = identity.OriginalNodeFileId ?? identity.NodeFileId,
                    Name = Path.GetFileName(normalizedPath),
                    SizeBytes = identity.SizeBytes,
                    ContentHash = identity.ContentHash ?? fileState.RemoteContentHash ?? string.Empty,
                    ETag = identity.ETag ?? fileState.RemoteETag ?? string.Empty,
                    CreatedAt = identity.UpdatedAt,
                    UpdatedAt = identity.UpdatedAt,
                },
                SyncPlaceholderHydrationState.RemoteOnly);
        }

        public static Guid ParseSyncPairId(string syncPairId)
        {
            if (!Guid.TryParse(syncPairId, out Guid parsed))
            {
                throw new ArgumentException(
                    "Virtual-files placeholder request contains an invalid sync pair id.",
                    nameof(syncPairId));
            }

            return parsed;
        }

        public static PlaceholderPath ResolvePlaceholderPath(
            string syncRootPath,
            string normalizedRelativePath)
        {
            string[] segments = normalizedRelativePath.Split('/');
            if (segments.Any(static segment => segment is "." or ".."))
            {
                throw new InvalidOperationException(
                    "Virtual-files placeholder paths cannot contain '.' or '..' segments.");
            }

            string root = Path.GetFullPath(syncRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string relativeFileName = segments[^1];
            string baseDirectoryPath = root;
            foreach (string segment in segments[..^1])
            {
                baseDirectoryPath = Path.Combine(baseDirectoryPath, segment);
            }

            baseDirectoryPath = Path.GetFullPath(baseDirectoryPath);
            string finalPath = Path.GetFullPath(Path.Combine(baseDirectoryPath, relativeFileName));
            if (!IsSamePathOrChild(baseDirectoryPath, root) || !IsSamePathOrChild(finalPath, root))
            {
                throw new InvalidOperationException("Virtual-files placeholder path escaped the sync root.");
            }

            return new PlaceholderPath(baseDirectoryPath, relativeFileName);
        }

        public static byte[] CreateSyncRootIdentity(Guid syncPairId, Guid remoteRootNodeId)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                product = WindowsCloudFilesProviderMetadata.ProviderId,
                syncPairId,
                remoteRootNodeId,
            });
        }

        public static byte[] CreateFileIdentity(
            RemoteFilePlaceholderRequest request,
            string normalizedPath)
        {
            return WindowsCloudFilesPlaceholderIdentity.Create(request, normalizedPath).ToBytes();
        }

        public static byte[] CreateDirectoryIdentity(
            RemoteDirectoryMaterializationRequest request,
            string normalizedPath)
        {
            return WindowsCloudFilesDirectoryPlaceholderIdentity.Create(request, normalizedPath).ToBytes();
        }

        public static WindowsCloudFilesNativePlaceholder CreateDirectoryNativePlaceholder(
            PlaceholderPath placeholderPath,
            byte[] directoryIdentity,
            NodeDto remoteDirectory)
        {
            return new WindowsCloudFilesNativePlaceholder(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName,
                directoryIdentity,
                0,
                remoteDirectory.CreatedAt,
                remoteDirectory.UpdatedAt,
                IsDirectory: true);
        }

        private static byte[] ValidateMissingFilePlaceholderArguments(
            SyncPairSettings syncPair,
            SyncStateEntry fileState)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(fileState);
            if (fileState.Kind != SyncEntryKind.File
                || fileState.PlaceholderIdentity is not { Length: > 0 } placeholderIdentity)
            {
                throw new InvalidOperationException(
                    "Cloud Files missing-placeholder recovery requires tracked file identity.");
            }

            return placeholderIdentity;
        }

        private static void ValidateMissingFilePlaceholderIdentity(
            SyncPairSettings syncPair,
            string normalizedPath,
            WindowsCloudFilesPlaceholderIdentity identity)
        {
            if (identity.SyncPairId != syncPair.Id
                || identity.RemoteRootNodeId != syncPair.RemoteRootNodeId
                || !string.Equals(
                    SyncPath.ToKey(identity.RelativePath),
                    SyncPath.ToKey(normalizedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Cloud Files missing-placeholder recovery found mismatched tracked identity.");
            }
        }

        private static bool IsSamePathOrChild(string candidatePath, string rootPath)
        {
            string normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
