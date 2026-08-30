// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class SyncStateSnapshotLoader(ISyncStateStore stateStore)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public async Task<(Dictionary<string, SyncStateEntry> DirectoryStateByPath, Dictionary<string, SyncStateEntry> FileStateByPath)> LoadAsync(
            string syncPairId,
            SyncRunOptions options,
            SyncTreeLookups treeLookups,
            CancellationToken cancellationToken)
        {
            if (options.Scope.IsFull)
            {
                return await LoadAllStateByPathAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            }

            List<string> keys = BuildUniquePathKeyList(
                treeLookups.LocalDirectoriesByPath.Keys,
                treeLookups.RemoteDirectoriesByPath.Keys,
                treeLookups.LocalFilesByPath.Keys,
                treeLookups.RemoteFilesByPath.Keys,
                BuildScopedPathKeys(options.Scope.LocalChangedPaths));
            (Dictionary<string, SyncStateEntry> directoryStateByPath, Dictionary<string, SyncStateEntry> fileStateByPath) =
                await LoadStateByPathAsync(syncPairId, keys, cancellationToken).ConfigureAwait(false);
            foreach (string deletedPath in BuildMinimalPathPrefixes(options.Scope.LocalDeletedPaths))
            {
                await foreach (SyncStateEntry entry in stateStore
                                   .LoadEntriesByPathPrefixAsync(syncPairId, deletedPath, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await AddEntryAsync(
                            syncPairId,
                            entry,
                            directoryStateByPath,
                            fileStateByPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return (directoryStateByPath, fileStateByPath);
        }

        private static IReadOnlyList<string> BuildMinimalPathPrefixes(IEnumerable<string> relativePaths)
        {
            List<string> prefixes = new List<string>();
            foreach (string path in relativePaths
                         .Select(SyncPath.Normalize)
                         .Where(static path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(PathComparer)
                         .OrderBy(static path => path.Count(static character => character == '/'))
                         .ThenBy(static path => path, PathComparer))
            {
                string key = SyncPath.ToKey(path);
                if (prefixes.Any(prefix => IsSameOrDescendantPathKey(key, SyncPath.ToKey(prefix))))
                {
                    continue;
                }

                prefixes.Add(path);
            }

            return prefixes;
        }

        private async Task<(Dictionary<string, SyncStateEntry> DirectoryStateByPath, Dictionary<string, SyncStateEntry> FileStateByPath)> LoadStateByPathAsync(
            string syncPairId,
            IEnumerable<string> keys,
            CancellationToken cancellationToken)
        {
            Dictionary<string, SyncStateEntry> directoryStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            Dictionary<string, SyncStateEntry> fileStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            await foreach (SyncStateEntry entry in stateStore.LoadEntriesByPathKeysAsync(syncPairId, keys, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                await AddEntryAsync(
                        syncPairId,
                        entry,
                        directoryStateByPath,
                        fileStateByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return (directoryStateByPath, fileStateByPath);
        }

        private async Task<(Dictionary<string, SyncStateEntry> DirectoryStateByPath, Dictionary<string, SyncStateEntry> FileStateByPath)> LoadAllStateByPathAsync(
            string syncPairId,
            CancellationToken cancellationToken)
        {
            Dictionary<string, SyncStateEntry> directoryStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            Dictionary<string, SyncStateEntry> fileStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            await foreach (SyncStateEntry entry in stateStore.LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                await AddEntryAsync(
                        syncPairId,
                        entry,
                        directoryStateByPath,
                        fileStateByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return (directoryStateByPath, fileStateByPath);
        }

        private async Task AddEntryAsync(
            string syncPairId,
            SyncStateEntry entry,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
            {
                await stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            string key = SyncPath.ToKey(entry.RelativePath);
            switch (entry.Kind)
            {
                case SyncEntryKind.Directory:
                    directoryStateByPath[key] = entry;
                    break;
                case SyncEntryKind.File:
                    fileStateByPath[key] = entry;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
            }
        }
    }
}
