// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.App.LocalChanges
{
    internal static class LocalChangeRequestFactory
    {
        public static SyncRunRequest? Create(
            string? localRootPath,
            SyncPairMode mode,
            PendingLocalSyncRequest request)
        {
            if (request.RequiresFullSync)
            {
                return SyncRunRequest.ForFull(request.Causes);
            }

            if (localRootPath is null)
            {
                return null;
            }

            bool allowRootRelativePath = mode == SyncPairMode.WindowsVirtualFiles;
            List<string> relativePaths = GetRelativePaths(
                localRootPath,
                request.ChangedPaths,
                allowRootRelativePath);
            if (relativePaths.Count == 0)
            {
                return null;
            }

            List<string> deletedRelativePaths = GetRelativePaths(
                localRootPath,
                request.DeletedPaths,
                allowRootRelativePath);
            return SyncRunRequest.ForLocalChangedPaths(relativePaths, deletedRelativePaths, request.Causes);
        }

        public static bool Record(
            string? localRootPath,
            SyncPairMode mode,
            PendingLocalSyncRequest pendingSync,
            LocalSyncRootChange change)
        {
            bool isWindowsVirtualFiles = mode == SyncPairMode.WindowsVirtualFiles;
            int maxScopedChangedPaths = isWindowsVirtualFiles
                ? PendingLocalSyncRequest.MaxWindowsVirtualFilesScopedChangedPaths
                : PendingLocalSyncRequest.MaxScopedChangedPaths;
            SyncRunCause fullSyncCause = GetFullSyncCause(change);
            if (fullSyncCause != SyncRunCause.None)
            {
                pendingSync.RecordChange(
                    change.FullPath,
                    fullSyncCause,
                    maxScopedChangedPaths,
                    isWindowsVirtualFiles);
                return true;
            }

            if (localRootPath is null)
            {
                return false;
            }

            bool recorded = TryRecordPath(
                localRootPath,
                change.FullPath,
                isWindowsVirtualFiles,
                pendingSync,
                maxScopedChangedPaths,
                change.Kind == LocalSyncRootChangeKind.Deleted);
            if (!string.IsNullOrWhiteSpace(change.OldFullPath))
            {
                recorded |= TryRecordPath(
                    localRootPath,
                    change.OldFullPath,
                    isWindowsVirtualFiles,
                    pendingSync,
                    maxScopedChangedPaths,
                    isDeleted: false);
            }

            return recorded;
        }

        private static List<string> GetRelativePaths(
            string localRootPath,
            IEnumerable<string> paths,
            bool allowRootRelativePath)
        {
            List<string> relativePaths = [];
            foreach (string path in paths)
            {
                if (TryGetSyncRelativePath(localRootPath, path, allowRootRelativePath, out string relativePath))
                {
                    relativePaths.Add(relativePath);
                }
            }

            return relativePaths;
        }

        private static bool TryRecordPath(
            string localRootPath,
            string fullPath,
            bool isWindowsVirtualFiles,
            PendingLocalSyncRequest pendingSync,
            int maxScopedChangedPaths,
            bool isDeleted)
        {
            if (!TryGetSyncRelativePath(localRootPath, fullPath, isWindowsVirtualFiles, out _))
            {
                return false;
            }

            pendingSync.RecordChange(
                fullPath,
                SyncRunCause.None,
                maxScopedChangedPaths,
                isWindowsVirtualFiles,
                isDeleted);
            return true;
        }

        private static SyncRunCause GetFullSyncCause(LocalSyncRootChange change)
        {
            if (change.Kind == LocalSyncRootChangeKind.Error)
            {
                return SyncRunCause.LocalWatcherError;
            }

            return change.Kind == LocalSyncRootChangeKind.Renamed && string.IsNullOrWhiteSpace(change.OldFullPath)
                ? SyncRunCause.LocalRenameRecovery
                : SyncRunCause.None;
        }

        private static bool TryGetSyncRelativePath(
            string localRootPath,
            string fullPath,
            bool allowRootRelativePath,
            out string relativePath)
        {
            return TryGetRelativePath(localRootPath, fullPath, allowRootRelativePath, out relativePath)
                && (string.Equals(relativePath, ".", StringComparison.Ordinal)
                    || !SyncPathIgnoreRules.ShouldIgnore(relativePath));
        }

        private static bool TryGetRelativePath(
            string localRootPath,
            string fullPath,
            bool allowRootRelativePath,
            out string relativePath)
        {
            try
            {
                string fullRoot = Path.GetFullPath(localRootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullChangedPath = Path.GetFullPath(fullPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullChangedPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = allowRootRelativePath ? "." : string.Empty;
                    return allowRootRelativePath;
                }

                string rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;
                if (!fullChangedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = string.Empty;
                    return false;
                }

                relativePath = Path.GetRelativePath(fullRoot, fullChangedPath).Replace('\\', '/');
                return !string.IsNullOrWhiteSpace(relativePath) && relativePath != ".";
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                relativePath = string.Empty;
                return false;
            }
        }
    }
}
