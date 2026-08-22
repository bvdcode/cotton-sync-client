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
            return await LoadStateByPathAsync(syncPairId, keys, cancellationToken).ConfigureAwait(false);
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
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string stateKey = SyncPath.ToKey(entry.RelativePath);
                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath[stateKey] = entry;
                        break;
                    case SyncEntryKind.File:
                        fileStateByPath[stateKey] = entry;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
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
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string key = SyncPath.ToKey(entry.RelativePath);
                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath.Add(key, entry);
                        break;
                    case SyncEntryKind.File:
                        fileStateByPath.Add(key, entry);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
            }

            return (directoryStateByPath, fileStateByPath);
        }
    }
}
