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
        private async Task<bool> TryHandleManualRootHydrationAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            HashSet<string> handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            if (diskState is null || !IsManualAlwaysKeepDirectoryCandidate(diskState.Attributes))
            {
                return false;
            }

            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            List<SyncStateEntry> subtreeEntries = new();
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    subtreeEntries.Add(entry);
                }
            }

            List<SyncStateEntry> hydratedEntries = new();
            (int hydratedFiles, int alreadyHydratedFiles) = await HydrateTrackedAvailabilityFilesAsync(
                    syncPair,
                    request,
                    subtreeEntries,
                    hydratedEntries,
                    handledAvailabilityPathKeys,
                    cancellationToken)
                .ConfigureAwait(false);

            await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            SyncStateEntry[] directoryEntries = subtreeEntries
                .Where(static entry => entry.Kind == SyncEntryKind.Directory)
                .OrderByDescending(static entry => GetPathDepth(entry.RelativePath))
                .ToArray();
            foreach (SyncStateEntry entry in directoryEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PinDirectoryIfNeeded(syncPair, entry.RelativePath);
                _cloudFiles.SetInSyncState(syncPair, entry.RelativePath);
            }

            _diagnostics.Record(
                "manual-always-keep-root",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                ".",
                "Hydrated "
                + hydratedFiles
                + " tracked files; "
                + alreadyHydratedFiles
                + " were already available; completed "
                + directoryEntries.Length
                + " tracked directories.");
            return true;
        }

        private async Task<bool> TryHandleManualDirectoryHydrationAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            string relativePath,
            HashSet<string> handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            (string NormalizedPath, WindowsVirtualFileDiskState DiskState)? context =
                await TryResolveTrackedVirtualDirectoryAsync(syncPair, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!context.HasValue
                || !IsManualAlwaysKeepDirectoryCandidate(context.Value.DiskState.Attributes))
            {
                return false;
            }

            string normalizedPath = context.Value.NormalizedPath;
            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            IReadOnlyList<SyncStateEntry> subtreeEntries = await LoadDirectorySubtreeEntriesAsync(
                    syncPair,
                    normalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            List<SyncStateEntry> hydratedEntries = new();
            (int hydratedFiles, int alreadyHydratedFiles) = await HydrateTrackedAvailabilityFilesAsync(
                    syncPair,
                    request,
                    subtreeEntries,
                    hydratedEntries,
                    handledAvailabilityPathKeys,
                    cancellationToken)
                .ConfigureAwait(false);

            await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            int completedDirectories = CompleteHydratedDirectories(syncPair, subtreeEntries, cancellationToken);
            handledAvailabilityPathKeys.Add(SyncPath.ToKey(normalizedPath));
            RecordDirectoryHydrationCompleted(
                syncPair,
                normalizedPath,
                hydratedFiles,
                alreadyHydratedFiles,
                completedDirectories);
            return true;
        }

        private async Task<(string NormalizedPath, WindowsVirtualFileDiskState DiskState)?>
            TryResolveTrackedVirtualDirectoryAsync(
                SyncPairSettings syncPair,
                string relativePath,
                CancellationToken cancellationToken)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return null;
            }

            SyncStateEntry? directoryState = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualDirectory(directoryState))
            {
                return null;
            }

            string? fullPath = TryResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            WindowsVirtualFileDiskState? diskState = fullPath is null ? null : TryReadDiskState(fullPath);
            return diskState is null ? null : (normalizedPath, diskState);
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadDirectorySubtreeEntriesAsync(
            SyncPairSettings syncPair,
            string normalizedPath,
            CancellationToken cancellationToken)
        {
            List<SyncStateEntry> subtreeEntries = [];
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadEntriesByPathPrefixAsync(
                                   syncPair.Id.ToString("D"),
                                   normalizedPath,
                                   cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                subtreeEntries.Add(entry);
            }

            return subtreeEntries;
        }

        private int CompleteHydratedDirectories(
            SyncPairSettings syncPair,
            IEnumerable<SyncStateEntry> subtreeEntries,
            CancellationToken cancellationToken)
        {
            SyncStateEntry[] directoryEntries = subtreeEntries
                .Where(static entry => entry.Kind == SyncEntryKind.Directory)
                .OrderByDescending(static entry => GetPathDepth(entry.RelativePath))
                .ToArray();
            foreach (SyncStateEntry entry in directoryEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PinDirectoryIfNeeded(syncPair, entry.RelativePath);
                _cloudFiles.SetInSyncState(syncPair, entry.RelativePath);
            }

            return directoryEntries.Length;
        }

        private void RecordDirectoryHydrationCompleted(
            SyncPairSettings syncPair,
            string normalizedPath,
            int hydratedFiles,
            int alreadyHydratedFiles,
            int completedDirectories)
        {
            _diagnostics.Record(
                "manual-always-keep-directory",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Hydrated "
                + hydratedFiles
                + " tracked files; "
                + alreadyHydratedFiles
                + " were already available; completed "
                + completedDirectories
                + " tracked directories.");
        }

        private void PinDirectoryIfNeeded(SyncPairSettings syncPair, string relativePath)
        {
            string fullPath = ResolveFullPath(syncPair.LocalRootPath, relativePath);
            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            if (diskState is not null && IsManualAlwaysKeepDirectoryCandidate(diskState.Attributes))
            {
                return;
            }

            _localChangeSuppression?.SuppressProviderWrite(
                syncPair.Id,
                syncPair.LocalRootPath,
                relativePath);
            _cloudFiles.PinPlaceholder(syncPair, relativePath);
        }

        private async Task<bool> TryHandleManualDirectoryUnpinAsync(
            SyncPairSettings syncPair,
            string relativePath,
            HashSet<string> handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            (string NormalizedPath, WindowsVirtualFileDiskState DiskState)? context =
                await TryResolveTrackedVirtualDirectoryAsync(syncPair, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!context.HasValue
                || !IsManualPinRemovalDirectoryCandidate(context.Value.DiskState.Attributes))
            {
                return false;
            }

            string normalizedPath = context.Value.NormalizedPath;
            IReadOnlyList<SyncStateEntry> subtreeEntries = await LoadDirectorySubtreeEntriesAsync(
                    syncPair,
                    normalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CanApplyDirectoryUnpin(syncPair, subtreeEntries, cancellationToken))
            {
                return false;
            }

            foreach (SyncStateEntry entry in subtreeEntries)
            {
                handledAvailabilityPathKeys.Add(SyncPath.ToKey(entry.RelativePath));
            }

            _diagnostics.Record(
                "manual-always-keep-directory",
                "unpinned",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer removed Always keep on this device without changing materialized file content.");
            return true;
        }

        private bool CanApplyDirectoryUnpin(
            SyncPairSettings syncPair,
            IEnumerable<SyncStateEntry> subtreeEntries,
            CancellationToken cancellationToken)
        {
            foreach (SyncStateEntry entry in subtreeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryPath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
                WindowsVirtualFileDiskState? diskState = TryReadDiskState(entryPath);
                if (!IsValidDirectoryUnpinEntry(entry, diskState))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidDirectoryUnpinEntry(
            SyncStateEntry entry,
            WindowsVirtualFileDiskState? diskState)
        {
            if (diskState is null)
            {
                return false;
            }

            if (entry.Kind == SyncEntryKind.Directory)
            {
                return IsManualPinRemovalDirectoryCandidate(diskState.Attributes);
            }

            return IsTrackedVirtualFile(entry)
                && IsManualPinRemovalFileCandidate(diskState.Attributes)
                && MaterializedBaselineMatches(entry, diskState);
        }

        private async Task<bool> TryHandleManualDirectoryDehydrationAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualDirectory(state))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = ResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            if (diskState is null || !IsManualFreeUpSpaceDirectoryCandidate(diskState.Attributes))
            {
                return false;
            }

            _diagnostics.Record(
                "manual-free-up-space-directory",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Free up space unpinned the tracked directory placeholder.");
            return true;
        }
    }
}
