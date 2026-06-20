// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;
using System.Text.Json;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsCloudFilesDirectoryPlaceholderIdentity(
        int Schema,
        string Product,
        Guid SyncPairId,
        Guid RemoteRootNodeId,
        string RelativePath,
        Guid NodeId,
        DateTime UpdatedAt)
    {
        private const int CurrentSchema = 1;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static WindowsCloudFilesDirectoryPlaceholderIdentity Create(
            RemoteDirectoryMaterializationRequest request,
            string normalizedPath)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!Guid.TryParse(request.SyncPairId, out Guid syncPairId))
            {
                throw new ArgumentException("Virtual-files directory placeholder request contains an invalid sync pair id.", nameof(request));
            }

            return new WindowsCloudFilesDirectoryPlaceholderIdentity(
                CurrentSchema,
                WindowsCloudFilesAdapter.ProviderId,
                syncPairId,
                request.RemoteRootNodeId,
                normalizedPath,
                request.RemoteDirectory.Id,
                request.RemoteDirectory.UpdatedAt);
        }

        public byte[] ToBytes()
        {
            byte[] identity = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
            if (identity.Length > WindowsCloudFilesPlaceholderIdentity.MaximumIdentityLength)
            {
                throw new InvalidOperationException(
                    "Virtual-files directory placeholder identity exceeds the Windows Cloud Files 4 KB limit.");
            }

            return identity;
        }
    }
}
