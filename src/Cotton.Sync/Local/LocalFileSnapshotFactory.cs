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
            LocalFileMetadata before = LocalFileContentHasher.ReadMetadata(file, relativePath);
            string contentHash = computeHash && !isCloudFilesOnlineOnlyPlaceholder
                ? await LocalFileContentHasher.ComputeAsync(
                    file.FullName,
                    relativePath,
                    progress: null,
                    before.Length,
                    cancellationToken)
                    .ConfigureAwait(false)
                : string.Empty;
            LocalFileMetadata after = LocalFileContentHasher.ReadMetadata(file, relativePath);
            if (before.Length != after.Length || before.LastWriteUtc != after.LastWriteUtc)
            {
                throw new LocalFileUnavailableException(relativePath, file.FullName, "the file changed during scanning.");
            }

            return new LocalFileSnapshot
            {
                RelativePath = relativePath,
                FullPath = file.FullName,
                ContentHash = contentHash,
                SizeBytes = after.Length,
                LastWriteUtc = after.LastWriteUtc,
                IsCloudFilesPlaceholder = isCloudFilesPlaceholder,
                IsCloudFilesOnlineOnlyPlaceholder = isCloudFilesOnlineOnlyPlaceholder,
            };
        }
    }
}
