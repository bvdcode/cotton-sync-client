// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal static class SyncTransferPlanner
    {
        public static void EnsureEnoughLocalFreeSpace(string localRootPath, string relativePath, long requiredBytes)
        {
            if (requiredBytes <= 0)
            {
                return;
            }

            long? availableFreeBytes = TryGetAvailableFreeBytes(localRootPath);
            if (!availableFreeBytes.HasValue || availableFreeBytes.Value >= requiredBytes)
            {
                return;
            }

            string displayPath = string.IsNullOrWhiteSpace(relativePath) ? "remote file" : relativePath;
            throw new LocalInsufficientDiskSpaceException(displayPath, requiredBytes, availableFreeBytes.Value);
        }

        private static long? TryGetAvailableFreeBytes(string localRootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            try
            {
                string fullRoot = Path.GetFullPath(localRootPath);
                Directory.CreateDirectory(fullRoot);
                string? driveRoot = Path.GetPathRoot(fullRoot);
                if (string.IsNullOrWhiteSpace(driveRoot))
                {
                    return null;
                }

                DriveInfo drive = new DriveInfo(driveRoot);
                return drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static void EnsureEnoughLocalFreeSpaceForPlannedDownloads(
            SyncPair syncPair,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            long? availableFreeBytes = TryGetAvailableFreeBytes(syncPair.LocalRootPath);
            if (!availableFreeBytes.HasValue)
            {
                return;
            }

            long simulatedFreeBytes = availableFreeBytes.Value;
            foreach (string key in pathKeys)
            {
                if (!TryCreatePlannedLocalDownload(
                        syncPair,
                        key,
                        localByPath,
                        remoteByPath,
                        stateByPath,
                        out string relativePath,
                        out long downloadBytes,
                        out long replacedLocalBytes))
                {
                    continue;
                }

                if (downloadBytes <= 0)
                {
                    continue;
                }

                if (simulatedFreeBytes < downloadBytes)
                {
                    throw new LocalInsufficientDiskSpaceException(relativePath, downloadBytes, simulatedFreeBytes);
                }

                simulatedFreeBytes += replacedLocalBytes - downloadBytes;
            }
        }

        public static long CalculatePlannedTransferBytesTotal(
            SyncPair syncPair,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            long totalBytes = 0;
            foreach (string key in pathKeys)
            {
                if (TryCalculatePlannedTransferBytes(syncPair, key, localByPath, remoteByPath, stateByPath, out long transferBytes)
                    && transferBytes > 0)
                {
                    totalBytes += transferBytes;
                }
            }

            return totalBytes;
        }

        public static long CalculatePlannedTransferBytes(
            SyncPair syncPair,
            string key,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            return TryCalculatePlannedTransferBytes(syncPair, key, localByPath, remoteByPath, stateByPath, out long transferBytes)
                ? transferBytes
                : 0;
        }

        private static bool TryCalculatePlannedTransferBytes(
            SyncPair syncPair,
            string key,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            out long transferBytes)
        {
            localByPath.TryGetValue(key, out LocalFileSnapshot? local);
            remoteByPath.TryGetValue(key, out RemoteFileSnapshot? remote);
            stateByPath.TryGetValue(key, out SyncStateEntry? state);

            if (state is null)
            {
                return TryCalculateUntrackedTransferBytes(syncPair, local, remote, out transferBytes);
            }

            if (TryCalculateOnlineOnlyPlaceholderTransferBytes(syncPair, state, local, remote, out transferBytes))
            {
                return transferBytes > 0;
            }

            if (local is not null && remote is not null && ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                transferBytes = 0;
                return false;
            }

            SyncFileChangeKind changeKind = ResolveTrackedFileChange(CreateFileChangeState(state, local, remote));
            return TryCalculateTrackedTransferBytes(changeKind, local, remote, out transferBytes);
        }

        private static bool TryCalculateOnlineOnlyPlaceholderTransferBytes(
            SyncPair syncPair,
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (local is null || !IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                transferBytes = 0;
                return false;
            }

            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            transferBytes = remoteChanged ? remote!.File.SizeBytes : 0;
            return true;
        }

        private static bool TryCalculateTrackedTransferBytes(
            SyncFileChangeKind changeKind,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            switch (changeKind)
            {
                case SyncFileChangeKind.Upload:
                    transferBytes = local!.SizeBytes;
                    return true;
                case SyncFileChangeKind.Download:
                    transferBytes = remote!.File.SizeBytes;
                    return true;
                case SyncFileChangeKind.Conflict:
                    return TryCalculateConflictTransferBytes(local, remote?.File, out transferBytes);
                case SyncFileChangeKind.None:
                case SyncFileChangeKind.DeleteState:
                case SyncFileChangeKind.DeleteLocal:
                case SyncFileChangeKind.DeleteRemote:
                    transferBytes = 0;
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null);
            }
        }

        private static bool TryCalculateUntrackedTransferBytes(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (local is null)
            {
                return TryCalculateRemoteOnlyTransferBytes(syncPair, remote, out transferBytes);
            }

            if (remote is null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (IsUntrackedRemoteReplacement(local, remote))
            {
                transferBytes = remote.File.SizeBytes;
                return true;
            }

            transferBytes = 0;
            return false;
        }

        private static bool TryCalculateRemoteOnlyTransferBytes(
            SyncPair syncPair,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (remote is null || syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                transferBytes = 0;
                return false;
            }

            transferBytes = remote.File.SizeBytes;
            return true;
        }

        private static bool IsUntrackedRemoteReplacement(
            LocalFileSnapshot local,
            RemoteFileSnapshot remote)
        {
            return !string.IsNullOrWhiteSpace(local.ContentHash)
                && !ContentMatches(local.ContentHash, remote.File.ContentHash);
        }

        private static bool TryCalculateConflictTransferBytes(
            LocalFileSnapshot? local,
            NodeFileManifestDto? remoteFile,
            out long transferBytes)
        {
            if (local is not null && remoteFile is null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (remoteFile is not null)
            {
                transferBytes = remoteFile.SizeBytes;
                return true;
            }

            transferBytes = 0;
            return false;
        }

        private static bool TryCreatePlannedLocalDownload(
            SyncPair syncPair,
            string key,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            out string relativePath,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            localByPath.TryGetValue(key, out LocalFileSnapshot? local);
            remoteByPath.TryGetValue(key, out RemoteFileSnapshot? remote);
            stateByPath.TryGetValue(key, out SyncStateEntry? state);
            relativePath = ResolvePlannedTransferRelativePath(key, local, remote, state);

            if (state is null)
            {
                return TryCreateRemoteOnlyDownload(syncPair, local, remote, out downloadBytes, out replacedLocalBytes);
            }

            if (TryCreateOnlineOnlyPlaceholderDownload(
                    syncPair,
                    state,
                    local,
                    remote,
                    out downloadBytes,
                    out replacedLocalBytes))
            {
                return downloadBytes > 0;
            }

            if (LocalAndRemoteContentMatch(local, remote))
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            SyncFileChangeKind changeKind = ResolveTrackedFileChange(CreateFileChangeState(state, local, remote));
            return TryCreateTrackedLocalDownload(changeKind, local, remote, out downloadBytes, out replacedLocalBytes);
        }

        private static string ResolvePlannedTransferRelativePath(
            string key,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            SyncStateEntry? state)
        {
            if (local is not null)
            {
                return local.RelativePath;
            }

            if (remote is not null)
            {
                return remote.RelativePath;
            }

            return state is not null ? state.RelativePath : key;
        }

        private static bool LocalAndRemoteContentMatch(
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return local is not null
                && remote is not null
                && ContentMatches(local.ContentHash, remote.File.ContentHash);
        }

        private static bool TryCreateOnlineOnlyPlaceholderDownload(
            SyncPair syncPair,
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            if (local is null || !IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            if (remoteChanged)
            {
                downloadBytes = remote!.File.SizeBytes;
            }
            else
            {
                downloadBytes = 0;
            }

            replacedLocalBytes = 0;
            return true;
        }

        private static bool TryCreateTrackedLocalDownload(
            SyncFileChangeKind changeKind,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            switch (changeKind)
            {
                case SyncFileChangeKind.Download:
                    downloadBytes = remote!.File.SizeBytes;
                    replacedLocalBytes = local?.SizeBytes ?? 0;
                    return true;
                case SyncFileChangeKind.Conflict:
                    return TryCreateConflictDownload(remote, out downloadBytes, out replacedLocalBytes);
                case SyncFileChangeKind.None:
                case SyncFileChangeKind.DeleteState:
                case SyncFileChangeKind.DeleteLocal:
                case SyncFileChangeKind.DeleteRemote:
                case SyncFileChangeKind.Upload:
                    downloadBytes = 0;
                    replacedLocalBytes = 0;
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null);
            }
        }

        private static bool TryCreateRemoteOnlyDownload(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            if (local is null && remote is not null)
            {
                if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles)
                {
                    downloadBytes = 0;
                    replacedLocalBytes = 0;
                    return false;
                }

                downloadBytes = remote.File.SizeBytes;
                replacedLocalBytes = 0;
                return true;
            }

            downloadBytes = 0;
            replacedLocalBytes = 0;
            return false;
        }

        private static bool TryCreateConflictDownload(
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            if (remote is null)
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            downloadBytes = remote.File.SizeBytes;
            replacedLocalBytes = 0;
            return true;
        }
    }
}
