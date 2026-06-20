// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Runtime.CompilerServices;

namespace Cotton.Sync.State
{
    /// <summary>
    /// Persists sync baselines for one or more sync pairs.
    /// </summary>
    public interface ISyncStateStore
    {
        /// <summary>
        /// Initializes durable storage if it has not been created yet.
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads all entries for a sync pair.
        /// </summary>
        Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(string syncPairId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams all entries for a sync pair.
        /// </summary>
        IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
            string syncPairId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams directory entries for a sync pair.
        /// </summary>
        async IAsyncEnumerable<SyncStateEntry> LoadPairDirectoryEntriesAsync(
            string syncPairId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await foreach (SyncStateEntry entry in LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Kind == SyncEntryKind.Directory)
                {
                    yield return entry;
                }
            }
        }

        /// <summary>
        /// Streams directory entries at or below a relative path prefix for a sync pair.
        /// </summary>
        async IAsyncEnumerable<SyncStateEntry> LoadDirectoryEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePathPrefix);
            string prefixKey = SyncPath.ToKey(relativePathPrefix);
            string childPrefix = prefixKey + "/";
            await foreach (SyncStateEntry entry in LoadPairDirectoryEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryKey = SyncPath.ToKey(entry.RelativePath);
                if (string.Equals(entryKey, prefixKey, StringComparison.OrdinalIgnoreCase)
                    || entryKey.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }

        /// <summary>
        /// Streams directory entries matching remote folder identifiers and file entries matching remote file identifiers.
        /// </summary>
        async IAsyncEnumerable<SyncStateEntry> LoadEntriesByRemoteIdsAsync(
            string syncPairId,
            IEnumerable<Guid> remoteNodeIds,
            IEnumerable<Guid> remoteFileIds,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(remoteNodeIds);
            ArgumentNullException.ThrowIfNull(remoteFileIds);
            HashSet<Guid> nodeIds = remoteNodeIds
                .Where(static id => id != Guid.Empty)
                .ToHashSet();
            HashSet<Guid> fileIds = remoteFileIds
                .Where(static id => id != Guid.Empty)
                .ToHashSet();
            if (nodeIds.Count == 0 && fileIds.Count == 0)
            {
                yield break;
            }

            await foreach (SyncStateEntry entry in LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Kind == SyncEntryKind.Directory
                        && entry.RemoteNodeId.HasValue
                        && nodeIds.Contains(entry.RemoteNodeId.Value))
                    || (entry.Kind == SyncEntryKind.File
                        && entry.RemoteFileId.HasValue
                        && fileIds.Contains(entry.RemoteFileId.Value)))
                {
                    yield return entry;
                }
            }
        }

        /// <summary>
        /// Streams entries for the specified relative path keys.
        /// </summary>
        async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(relativePathKeys);
            foreach (string key in relativePathKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(key) || SyncPathIgnoreRules.ShouldIgnore(key))
                {
                    continue;
                }

                SyncStateEntry? entry = await GetAsync(syncPairId, key, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }

        /// <summary>
        /// Loads the latest sync timestamp for a sync pair without materializing all entries.
        /// </summary>
        Task<DateTime?> GetPairLastSyncedAtUtcAsync(string syncPairId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the remote change-feed checkpoint for a sync pair.
        /// </summary>
        Task<SyncChangeCursor> GetChangeCursorAsync(string syncPairId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads one entry by relative path.
        /// </summary>
        Task<SyncStateEntry?> GetAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts or updates one sync entry.
        /// </summary>
        Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts or updates several sync entries as one durable write batch when supported.
        /// </summary>
        async Task UpsertManyAsync(
            IReadOnlyCollection<SyncStateEntry> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);
            foreach (SyncStateEntry entry in entries)
            {
                await UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Inserts or updates the remote change-feed checkpoint for a sync pair.
        /// </summary>
        Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes one sync entry by relative path.
        /// </summary>
        Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes all persisted state for a sync pair.
        /// </summary>
        Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Replaces every entry for a sync pair with a new snapshot.
        /// </summary>
        Task ReplacePairAsync(string syncPairId, IReadOnlyCollection<SyncStateEntry> entries, CancellationToken cancellationToken = default);
    }
}
