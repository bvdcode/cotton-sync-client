// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal static class VirtualFilesPlaceholderAdoptionPolicy
    {
        public static bool CanAdopt(LocalFileSnapshot local, NodeFileManifestDto remoteFile)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder
                && local.SizeBytes == remoteFile.SizeBytes
                && DateTimesMatchWithinCloudFilesMetadataTolerance(local.LastWriteUtc, remoteFile.UpdatedAt);
        }
    }
}
