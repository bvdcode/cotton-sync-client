// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Collections.Concurrent;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private async Task<HashSet<string>> FindManualDehydrationPathKeysAsync(
            SyncPairSettings syncPair,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken)
        {
            List<(string PathKey, bool IsDirectory, FileAttributes Attributes)> snapshots = [];
            foreach (string relativePath in relativePaths)
            {
                (string PathKey, bool IsDirectory, FileAttributes Attributes)? snapshot =
                    await TryReadAvailabilitySnapshotAsync(syncPair, relativePath, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.HasValue)
                {
                    snapshots.Add(snapshot.Value);
                }
            }

            string[] neutralDirectoryKeys = snapshots
                .Where(static snapshot => IsNeutralAvailabilityDirectorySnapshot(snapshot))
                .Select(static snapshot => snapshot.PathKey)
                .ToArray();
            return snapshots
                .Where(static snapshot => IsManualDehydrationFileSnapshot(snapshot))
                .Where(snapshot => !IsInsideAnyDirectory(snapshot.PathKey, neutralDirectoryKeys))
                .Select(static snapshot => snapshot.PathKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<(string PathKey, bool IsDirectory, FileAttributes Attributes)?>
            TryReadAvailabilitySnapshotAsync(
                SyncPairSettings syncPair,
                string relativePath,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRootRelativePath(relativePath)
                || !TryNormalizePath(relativePath, out string normalizedPath))
            {
                return null;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            bool isDirectory = IsTrackedVirtualDirectory(state);
            if (!isDirectory && !IsTrackedVirtualFile(state))
            {
                return null;
            }

            string? fullPath = TryResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            WindowsVirtualFileDiskState? diskState = fullPath is null ? null : TryReadDiskState(fullPath);
            return diskState is null
                ? null
                : (SyncPath.ToKey(normalizedPath), isDirectory, diskState.Attributes);
        }

        private static bool IsNeutralAvailabilityDirectorySnapshot(
            (string PathKey, bool IsDirectory, FileAttributes Attributes) snapshot)
        {
            return snapshot.IsDirectory && IsManualPinRemovalDirectoryCandidate(snapshot.Attributes);
        }

        private static bool IsManualDehydrationFileSnapshot(
            (string PathKey, bool IsDirectory, FileAttributes Attributes) snapshot)
        {
            return !snapshot.IsDirectory
                && (IsManualFreeUpSpaceCandidate(snapshot.Attributes)
                    || IsCompletedManualFreeUpSpaceCandidate(snapshot.Attributes));
        }

        private static bool IsInsideAnyDirectory(string pathKey, IReadOnlyList<string> directoryKeys)
        {
            return directoryKeys.Any(directoryKey => IsSameOrDescendantPathKey(pathKey, directoryKey));
        }

        private static bool IsSameOrDescendantPathKey(string pathKey, string directoryKey)
        {
            return string.Equals(pathKey, directoryKey, StringComparison.OrdinalIgnoreCase)
                || pathKey.StartsWith(directoryKey.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
