// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.Desktop.Platform.WindowsCloudFilesPlaceholderFactory;

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsCloudFilesUploadFinalizationPolicy
    {
        public static WindowsCloudFilesUploadFinalizationPreparation Prepare(
            SyncPairSettings syncPair,
            WindowsCloudFilesSyncRootRegistration registration,
            SyncStateEntry fileState,
            Func<string, bool> isReparsePoint)
        {
            ValidateArguments(syncPair, fileState);
            string normalizedPath = SyncPath.Normalize(fileState.RelativePath);
            PlaceholderPath placeholderPath = ResolvePlaceholderPath(
                registration.LocalRootPath,
                normalizedPath);
            string fullPlaceholderPath = Path.Combine(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName);
            EnsureLocalFileExists(fullPlaceholderPath);
            byte[] fileIdentity = CreateUploadedFileIdentity(
                syncPair,
                registration.LocalRootPath,
                normalizedPath,
                fullPlaceholderPath,
                fileState);
            string expectedContentHash = RequireLocalContentHash(fileState.LocalContentHash);
            long expectedSizeBytes = RequireLocalSize(fileState.LocalSizeBytes);
            DateTime expectedLastWriteUtc = RequireLocalLastWrite(fileState.LocalLastWriteUtc);
            WindowsCloudFilesUploadedFileFinalizationMode mode = isReparsePoint(fullPlaceholderPath)
                ? WindowsCloudFilesUploadedFileFinalizationMode.UpdateExistingPlaceholder
                : WindowsCloudFilesUploadedFileFinalizationMode.ConvertRegularFile;
            WindowsCloudFilesNativePlaceholder nativePlaceholder = new(
                placeholderPath.BaseDirectoryPath,
                placeholderPath.RelativeFileName,
                fileIdentity,
                expectedSizeBytes,
                fileState.SyncedAtUtc.ToUniversalTime(),
                expectedLastWriteUtc);
            WindowsCloudFilesUploadedFileFinalizationRequest request = new(
                nativePlaceholder,
                expectedContentHash,
                expectedSizeBytes,
                expectedLastWriteUtc,
                mode);
            return new WindowsCloudFilesUploadFinalizationPreparation(
                normalizedPath,
                fullPlaceholderPath,
                fileIdentity,
                request);
        }

        private static void ValidateArguments(SyncPairSettings syncPair, SyncStateEntry fileState)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(fileState);
            if (fileState.Kind != SyncEntryKind.File)
            {
                throw new InvalidOperationException(
                    "Uploaded Cloud Files finalization requires a file state entry.");
            }
        }

        private static void EnsureLocalFileExists(string fullPlaceholderPath)
        {
            if (!File.Exists(fullPlaceholderPath))
            {
                throw new FileNotFoundException(
                    "Uploaded Cloud Files placeholder finalization requires the uploaded local file.",
                    fullPlaceholderPath);
            }
        }

        private static byte[] CreateUploadedFileIdentity(
            SyncPairSettings syncPair,
            string localRootPath,
            string normalizedPath,
            string fullPlaceholderPath,
            SyncStateEntry fileState)
        {
            Guid remoteFileId = RequireRemoteIdentity(fileState.RemoteFileId);
            Guid remoteNodeId = RequireRemoteIdentity(fileState.RemoteNodeId);
            Guid remoteFileManifestId = RequireRemoteIdentity(fileState.RemoteFileManifestId);
            string remoteContentHash = RequireRemoteContentHash(fileState.RemoteContentHash);
            long sizeBytes = fileState.RemoteSizeBytes
                ?? fileState.LocalSizeBytes
                ?? new FileInfo(fullPlaceholderPath).Length;
            DateTime updatedAt = fileState.LocalLastWriteUtc?.ToUniversalTime()
                ?? fileState.SyncedAtUtc.ToUniversalTime();
            return CreateFileIdentity(
                new RemoteFilePlaceholderRequest(
                    syncPair.Id.ToString("D"),
                    localRootPath,
                    syncPair.RemoteRootNodeId,
                    normalizedPath,
                    new NodeFileManifestDto
                    {
                        Id = remoteFileId,
                        NodeId = remoteNodeId,
                        FileManifestId = remoteFileManifestId,
                        OriginalNodeFileId = fileState.RemoteOriginalNodeFileId ?? remoteFileId,
                        SizeBytes = sizeBytes,
                        ContentHash = remoteContentHash,
                        ETag = fileState.RemoteETag ?? string.Empty,
                        CreatedAt = fileState.SyncedAtUtc.ToUniversalTime(),
                        UpdatedAt = updatedAt,
                        Name = Path.GetFileName(normalizedPath),
                    }),
                normalizedPath);
        }

        private static Guid RequireRemoteIdentity(Guid? value)
        {
            return value ?? throw new InvalidOperationException(
                "Uploaded Cloud Files placeholder finalization requires remote file identity in sync state.");
        }

        private static string RequireRemoteContentHash(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "Uploaded Cloud Files placeholder finalization requires remote file identity in sync state.");
            }

            return value;
        }

        private static string RequireLocalContentHash(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "Uploaded Cloud Files placeholder finalization requires the uploaded local content hash.");
            }

            return value;
        }

        private static long RequireLocalSize(long? value)
        {
            return value ?? throw new InvalidOperationException(
                "Uploaded Cloud Files placeholder finalization requires the uploaded local file size.");
        }

        private static DateTime RequireLocalLastWrite(DateTime? value)
        {
            return value?.ToUniversalTime() ?? throw new InvalidOperationException(
                "Uploaded Cloud Files placeholder finalization requires the uploaded local write timestamp.");
        }
    }
}
