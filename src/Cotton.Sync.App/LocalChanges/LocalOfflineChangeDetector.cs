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

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalOfflineChangeDetector" /> class.
        /// </summary>
        public LocalOfflineChangeDetector(
            ILocalFileMetadataTreeLookupScanner localScanner,
            ISyncStateStore stateStore)
        {
            _localScanner = localScanner ?? throw new ArgumentNullException(nameof(localScanner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
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
            var changedPaths = new HashSet<string>(PathComparer);
            var changedDirectoryPaths = new HashSet<string>(PathComparer);
            var deletedPaths = new HashSet<string>(PathComparer);
            var deletedDirectoryPaths = new HashSet<string>(PathComparer);
            await foreach (SyncStateEntry state in _stateStore
                               .LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = SyncPath.ToKey(state.RelativePath);
                if (state.Kind == SyncEntryKind.Directory)
                {
                    if (local.DirectoriesByPath.Remove(key))
                    {
                        continue;
                    }

                    if (local.FilesByPath.Remove(key, out LocalFileSnapshot? replacementFile))
                    {
                        changedPaths.Add(replacementFile.RelativePath);
                        continue;
                    }

                    deletedPaths.Add(state.RelativePath);
                    deletedDirectoryPaths.Add(state.RelativePath);
                    continue;
                }

                if (state.Kind == SyncEntryKind.File)
                {
                    if (local.FilesByPath.Remove(key, out LocalFileSnapshot? localFile))
                    {
                        if (HasFileChanged(localFile, state))
                        {
                            changedPaths.Add(localFile.RelativePath);
                        }

                        continue;
                    }

                    if (local.DirectoriesByPath.Remove(key, out LocalDirectorySnapshot? replacementDirectory))
                    {
                        changedPaths.Add(replacementDirectory.RelativePath);
                        changedDirectoryPaths.Add(replacementDirectory.RelativePath);
                        continue;
                    }
                }

                deletedPaths.Add(state.RelativePath);
            }

            foreach (LocalDirectorySnapshot directory in local.DirectoriesByPath.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changedPaths.Add(directory.RelativePath);
                changedDirectoryPaths.Add(directory.RelativePath);
            }

            foreach (LocalFileSnapshot file in local.FilesByPath.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changedPaths.Add(file.RelativePath);
            }

            CollapseDescendants(changedPaths, changedDirectoryPaths);
            CollapseDescendants(deletedPaths, deletedDirectoryPaths);
            changedPaths.UnionWith(deletedPaths);
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
