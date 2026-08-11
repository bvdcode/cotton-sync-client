// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Compares a metadata-only local snapshot with durable sync state after a watcher gap.
    /// </summary>
    public class LocalOfflineChangeDetector : ILocalOfflineChangeDetector
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        private readonly ILocalFileMetadataTreeLookupScanner _localScanner;
        private readonly ISyncStateStore _stateStore;
        private readonly ILocalProviderFileMarker? _providerFileMarker;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalOfflineChangeDetector" /> class.
        /// </summary>
        public LocalOfflineChangeDetector(
            ILocalFileMetadataTreeLookupScanner localScanner,
            ISyncStateStore stateStore,
            ILocalProviderFileMarker? providerFileMarker = null)
        {
            _localScanner = localScanner ?? throw new ArgumentNullException(nameof(localScanner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _providerFileMarker = providerFileMarker;
        }

        /// <inheritdoc />
        public async Task<SyncRunRequest?> DetectAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return null;
            }

            string syncPairId = syncPair.Id.ToString("D");
            SyncChangeCursor cursor = await _stateStore
                .GetChangeCursorAsync(syncPairId, cancellationToken)
                .ConfigureAwait(false);
            if (!cursor.HasCompletedFullReconcile)
            {
                return null;
            }

            LocalTreeLookupSnapshot local = await _localScanner
                .ScanTreeMetadataLookupsAsync(syncPair.LocalRootPath, progress: null, cancellationToken)
                .ConfigureAwait(false);
            HashSet<string> changedPaths = new(PathComparer);
            HashSet<string> changedDirectoryPaths = new(PathComparer);
            HashSet<string> deletedPaths = new(PathComparer);
            HashSet<string> deletedDirectoryPaths = new(PathComparer);
            await ReconcileTrackedEntriesAsync(
                    syncPairId,
                    local,
                    changedPaths,
                    changedDirectoryPaths,
                    deletedPaths,
                    deletedDirectoryPaths,
                    cancellationToken)
                .ConfigureAwait(false);
            AddUntrackedDirectories(local, changedPaths, changedDirectoryPaths, cancellationToken);
            await AddUntrackedFilesAsync(syncPair, local, changedPaths, cancellationToken).ConfigureAwait(false);

            CollapseDescendants(changedPaths, changedDirectoryPaths);
            CollapseDescendants(deletedPaths, deletedDirectoryPaths);
            changedPaths.UnionWith(deletedPaths);
            return CreateSyncRunRequest(changedPaths, deletedPaths);
        }

        private async Task ReconcileTrackedEntriesAsync(
            string syncPairId,
            LocalTreeLookupSnapshot local,
            HashSet<string> changedPaths,
            HashSet<string> changedDirectoryPaths,
            HashSet<string> deletedPaths,
            HashSet<string> deletedDirectoryPaths,
            CancellationToken cancellationToken)
        {
            await foreach (SyncStateEntry state in _stateStore
                               .LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReconcileTrackedEntry(
                    state,
                    local,
                    changedPaths,
                    changedDirectoryPaths,
                    deletedPaths,
                    deletedDirectoryPaths);
            }
        }

        private static void AddUntrackedDirectories(
            LocalTreeLookupSnapshot local,
            HashSet<string> changedPaths,
            HashSet<string> changedDirectoryPaths,
            CancellationToken cancellationToken)
        {
            foreach (LocalDirectorySnapshot directory in local.DirectoriesByPath.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changedPaths.Add(directory.RelativePath);
                changedDirectoryPaths.Add(directory.RelativePath);
            }
        }

        private async Task AddUntrackedFilesAsync(
            SyncPairSettings syncPair,
            LocalTreeLookupSnapshot local,
            HashSet<string> changedPaths,
            CancellationToken cancellationToken)
        {
            foreach (LocalFileSnapshot file in local.FilesByPath.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_providerFileMarker is not null
                    && await _providerFileMarker
                        .IsUnchangedAsync(syncPair.Id, syncPair.LocalRootPath, file, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                changedPaths.Add(file.RelativePath);
            }
        }

        private static SyncRunRequest? CreateSyncRunRequest(
            HashSet<string> changedPaths,
            HashSet<string> deletedPaths)
        {
            if (changedPaths.Count == 0)
            {
                return null;
            }

            if (changedPaths.Count > PendingLocalSyncRequest.MaxWindowsVirtualFilesScopedChangedPaths)
            {
                return SyncRunRequest.ForFull(SyncRunCause.LocalChange | SyncRunCause.LocalChangeOverflow);
            }

            return SyncRunRequest.ForLocalChangedPaths(
                changedPaths.OrderBy(static path => path, PathComparer),
                deletedPaths.OrderBy(static path => path, PathComparer),
                SyncRunCause.LocalChange);
        }

        private static void ReconcileTrackedEntry(
            SyncStateEntry state,
            LocalTreeLookupSnapshot local,
            HashSet<string> changedPaths,
            HashSet<string> changedDirectoryPaths,
            HashSet<string> deletedPaths,
            HashSet<string> deletedDirectoryPaths)
        {
            string key = SyncPath.ToKey(state.RelativePath);
            switch (state.Kind)
            {
                case SyncEntryKind.Directory:
                    ReconcileTrackedDirectory(
                        state,
                        key,
                        local,
                        changedPaths,
                        deletedPaths,
                        deletedDirectoryPaths);
                    return;
                case SyncEntryKind.File:
                    if (ReconcileTrackedFile(state, key, local, changedPaths, changedDirectoryPaths))
                    {
                        return;
                    }

                    break;
                default:
                    break;
            }

            deletedPaths.Add(state.RelativePath);
        }

        private static void ReconcileTrackedDirectory(
            SyncStateEntry state,
            string key,
            LocalTreeLookupSnapshot local,
            HashSet<string> changedPaths,
            HashSet<string> deletedPaths,
            HashSet<string> deletedDirectoryPaths)
        {
            if (local.DirectoriesByPath.Remove(key))
            {
                return;
            }

            if (local.FilesByPath.Remove(key, out LocalFileSnapshot? replacementFile))
            {
                changedPaths.Add(replacementFile.RelativePath);
                return;
            }

            deletedPaths.Add(state.RelativePath);
            deletedDirectoryPaths.Add(state.RelativePath);
        }

        private static bool ReconcileTrackedFile(
            SyncStateEntry state,
            string key,
            LocalTreeLookupSnapshot local,
            HashSet<string> changedPaths,
            HashSet<string> changedDirectoryPaths)
        {
            if (local.FilesByPath.Remove(key, out LocalFileSnapshot? localFile))
            {
                if (HasFileChanged(localFile, state))
                {
                    changedPaths.Add(localFile.RelativePath);
                }

                return true;
            }

            if (!local.DirectoriesByPath.Remove(key, out LocalDirectorySnapshot? replacementDirectory))
            {
                return false;
            }

            changedPaths.Add(replacementDirectory.RelativePath);
            changedDirectoryPaths.Add(replacementDirectory.RelativePath);
            return true;
        }

        private static bool HasFileChanged(LocalFileSnapshot local, SyncStateEntry state)
        {
            if (state.LocalLastWriteUtc.HasValue && state.LocalSizeBytes.HasValue)
            {
                return state.LocalSizeBytes.Value != local.SizeBytes
                    || state.LocalLastWriteUtc.Value.ToUniversalTime() != local.LastWriteUtc.ToUniversalTime();
            }

            if (local.IsCloudFilesOnlineOnlyPlaceholder
                && state.PlaceholderHydrationState is SyncPlaceholderHydrationState.RemoteOnly
                    or SyncPlaceholderHydrationState.Dehydrated)
            {
                return false;
            }

            return true;
        }

        private static void CollapseDescendants(
            HashSet<string> paths,
            IReadOnlySet<string> directoryPaths)
        {
            if (paths.Count == 0 || directoryPaths.Count == 0)
            {
                return;
            }

            HashSet<string> directoryKeys = directoryPaths
                .Select(SyncPath.ToKey)
                .ToHashSet(PathComparer);
            paths.RemoveWhere(path => HasAncestorDirectory(SyncPath.ToKey(path), directoryKeys));
        }

        private static bool HasAncestorDirectory(string pathKey, IReadOnlySet<string> directoryKeys)
        {
            int separatorIndex = pathKey.LastIndexOf('/');
            while (separatorIndex > 0)
            {
                string parentKey = pathKey[..separatorIndex];
                if (directoryKeys.Contains(parentKey))
                {
                    return true;
                }

                separatorIndex = parentKey.LastIndexOf('/');
            }

            return false;
        }
    }
}
