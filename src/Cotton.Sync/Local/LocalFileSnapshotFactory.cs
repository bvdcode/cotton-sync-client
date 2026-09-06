// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Local
{
    internal static class LocalFileSnapshotFactory
    {
        public static async Task<LocalFileSnapshot> CreateAsync(
            FileInfo file,
            string relativePath,
            bool computeHash,
            bool isCloudFilesPlaceholder,
            bool isCloudFilesOnlineOnlyPlaceholder,
            CancellationToken cancellationToken)
        {
            LocalFilePlatformProbe.ValidatePermissions(file, relativePath);
            LocalFileMetadata metadata = LocalFileContentHasher.ReadMetadata(file, relativePath);
            string contentHash = string.Empty;
            if (computeHash && !isCloudFilesOnlineOnlyPlaceholder)
            {
                contentHash = await LocalFileContentHasher.ComputeAsync(
                        file.FullName,
                        relativePath,
                        progress: null,
                        metadata.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                LocalFileMetadata after = LocalFileContentHasher.ReadMetadata(file, relativePath);
                if (metadata.Length != after.Length || metadata.LastWriteUtc != after.LastWriteUtc)
                {
                    throw new LocalFileUnavailableException(relativePath, file.FullName, "the file changed during scanning.");
                }

                metadata = after;
            }

            return new LocalFileSnapshot
            {
                RelativePath = relativePath,
                FullPath = file.FullName,
                ContentHash = contentHash,
                SizeBytes = metadata.Length,
                LastWriteUtc = metadata.LastWriteUtc,
                IsCloudFilesPlaceholder = isCloudFilesPlaceholder,
                IsCloudFilesOnlineOnlyPlaceholder = isCloudFilesOnlineOnlyPlaceholder,
            };
        }
    }
}
