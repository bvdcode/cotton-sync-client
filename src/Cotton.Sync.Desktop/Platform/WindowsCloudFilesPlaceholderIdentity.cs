// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesPlaceholderIdentity(
        int Schema,
        string Product,
        Guid SyncPairId,
        Guid RemoteRootNodeId,
        string RelativePath,
        Guid NodeFileId,
        Guid NodeId,
        Guid FileManifestId,
        Guid? OriginalNodeFileId,
        long SizeBytes,
        string? ContentHash,
        string? ETag,
        DateTime UpdatedAt)
    {
        public const int CurrentSchema = 1;
        public const int MaximumIdentityLength = 4096;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static WindowsCloudFilesPlaceholderIdentity Create(
            RemoteFilePlaceholderRequest request,
            string normalizedPath)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!Guid.TryParse(request.SyncPairId, out Guid syncPairId))
            {
                throw new ArgumentException("Virtual-files placeholder request contains an invalid sync pair id.", nameof(request));
            }

            return new WindowsCloudFilesPlaceholderIdentity(
                CurrentSchema,
                WindowsCloudFilesAdapter.ProviderId,
                syncPairId,
                request.RemoteRootNodeId,
                normalizedPath,
                request.RemoteFile.Id,
                request.RemoteFile.NodeId,
                request.RemoteFile.FileManifestId,
                request.RemoteFile.OriginalNodeFileId,
                request.RemoteFile.SizeBytes,
                request.RemoteFile.ContentHash,
                request.RemoteFile.ETag,
                request.RemoteFile.UpdatedAt);
        }

        public byte[] ToBytes()
        {
            byte[] identity = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
            if (identity.Length > MaximumIdentityLength)
            {
                throw new InvalidOperationException(
                    "Virtual-files placeholder identity exceeds the Windows Cloud Files 4 KB limit.");
            }

            return identity;
        }

        public static WindowsCloudFilesPlaceholderIdentity Parse(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            WindowsCloudFilesPlaceholderIdentity? identity =
                JsonSerializer.Deserialize<WindowsCloudFilesPlaceholderIdentity>(bytes, JsonOptions);
            if (identity is null)
            {
                throw new InvalidOperationException("Virtual-files placeholder identity is empty.");
            }

            if (identity.Schema != CurrentSchema || identity.Product != WindowsCloudFilesAdapter.ProviderId)
            {
                throw new InvalidOperationException("Virtual-files placeholder identity belongs to an unsupported schema.");
            }

            return identity;
        }
    }
}
