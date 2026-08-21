// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SyncStateLookupReader(
        SyncStateDbContextFactory contextFactory,
        SyncStateStoreInitializer initializer)
    {
        private const int LookupBatchSize = 500;

        public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByRemoteIdsAsync(
            string syncPairId,
            IEnumerable<Guid> remoteNodeIds,
            IEnumerable<Guid> remoteFileIds,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(remoteNodeIds);
            ArgumentNullException.ThrowIfNull(remoteFileIds);
            Guid[] nodeIds = remoteNodeIds
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .OrderBy(static id => id)
                .ToArray();
            Guid[] fileIds = remoteFileIds
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .OrderBy(static id => id)
                .ToArray();
            if (nodeIds.Length == 0 && fileIds.Length == 0)
            {
                yield break;
            }

            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> yieldedKeys = new(StringComparer.OrdinalIgnoreCase);
            foreach (IReadOnlyCollection<Guid> batch in CreateBatches(nodeIds, LookupBatchSize))
            {
                IReadOnlyList<SyncStateEntry> entries =
                    await LoadEntriesByRemoteNodeIdBatchAsync(syncPairId, batch, cancellationToken).ConfigureAwait(false);
                foreach (SyncStateEntry entry in entries)
                {
                    if (yieldedKeys.Add(SyncPath.ToKey(entry.RelativePath)))
                    {
                        yield return entry;
                    }
                }
            }

            foreach (IReadOnlyCollection<Guid> batch in CreateBatches(fileIds, LookupBatchSize))
            {
                IReadOnlyList<SyncStateEntry> entries =
                    await LoadEntriesByRemoteFileIdBatchAsync(syncPairId, batch, cancellationToken).ConfigureAwait(false);
                foreach (SyncStateEntry entry in entries)
                {
                    if (yieldedKeys.Add(SyncPath.ToKey(entry.RelativePath)))
                    {
                        yield return entry;
                    }
                }
            }
        }

        public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (IReadOnlyCollection<string> batch in CreatePathKeyBatchesAsync(
                               syncPairId,
                               relativePathKeys,
                               cancellationToken))
            {
                IReadOnlyList<SyncStateEntry> entries =
                    await LoadEntriesByPathKeyBatchAsync(syncPairId, batch, cancellationToken).ConfigureAwait(false);
                foreach (SyncStateEntry entry in entries)
                {
                    yield return entry;
                }
            }
        }

        public async IAsyncEnumerable<SyncVirtualFilesResumeEntry> LoadVirtualFilesResumeEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (IReadOnlyCollection<string> batch in CreatePathKeyBatchesAsync(
                               syncPairId,
                               relativePathKeys,
                               cancellationToken))
            {
                IReadOnlyList<SyncVirtualFilesResumeEntry> entries =
                    await LoadVirtualFilesResumeEntriesByPathKeyBatchAsync(syncPairId, batch, cancellationToken)
                        .ConfigureAwait(false);
                foreach (SyncVirtualFilesResumeEntry entry in entries)
                {
                    yield return entry;
                }
            }
        }

        private async IAsyncEnumerable<IReadOnlyCollection<string>> CreatePathKeyBatchesAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(relativePathKeys);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            List<string> keyBatch = new(LookupBatchSize);
            foreach (string key in relativePathKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(key) || SyncPathIgnoreRules.ShouldIgnore(key))
                {
                    continue;
                }

                string normalizedKey = SyncPath.ToKey(key);
                if (!keyBatch.Contains(normalizedKey, StringComparer.OrdinalIgnoreCase))
                {
                    keyBatch.Add(normalizedKey);
                }

                if (keyBatch.Count >= LookupBatchSize)
                {
                    yield return keyBatch.ToArray();
                    keyBatch.Clear();
                }
            }

            if (keyBatch.Count > 0)
            {
                yield return keyBatch.ToArray();
            }
        }

        private static IEnumerable<IReadOnlyCollection<T>> CreateBatches<T>(IReadOnlyList<T> items, int batchSize)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
            for (int index = 0; index < items.Count; index += batchSize)
            {
                int count = Math.Min(batchSize, items.Count - index);
                T[] batch = new T[count];
                for (int offset = 0; offset < count; offset++)
                {
                    batch[offset] = items[index + offset];
                }

                yield return batch;
            }
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadEntriesByPathKeyBatchAsync(
            string syncPairId,
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken)
        {
            await using SyncStateDbContext context = contextFactory.Create();
            List<SyncStateEntity> entities = await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId && keys.Contains(entry.RelativePathKey))
                .OrderBy(entry => entry.RelativePathKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(SyncStateEntityMapper.ToModel).ToArray();
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadEntriesByRemoteNodeIdBatchAsync(
            string syncPairId,
            IReadOnlyCollection<Guid> remoteNodeIds,
            CancellationToken cancellationToken)
        {
            await using SyncStateDbContext context = contextFactory.Create();
            List<SyncStateEntity> entities = await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId
                    && entry.Kind == SyncEntryKind.Directory
                    && entry.RemoteNodeId.HasValue
                    && remoteNodeIds.Contains(entry.RemoteNodeId.Value))
                .OrderBy(entry => entry.RelativePathKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(SyncStateEntityMapper.ToModel).ToArray();
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadEntriesByRemoteFileIdBatchAsync(
            string syncPairId,
            IReadOnlyCollection<Guid> remoteFileIds,
            CancellationToken cancellationToken)
        {
            await using SyncStateDbContext context = contextFactory.Create();
            List<SyncStateEntity> entities = await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId
                    && entry.Kind == SyncEntryKind.File
                    && entry.RemoteFileId.HasValue
                    && remoteFileIds.Contains(entry.RemoteFileId.Value))
                .OrderBy(entry => entry.RelativePathKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(SyncStateEntityMapper.ToModel).ToArray();
        }

        private async Task<IReadOnlyList<SyncVirtualFilesResumeEntry>> LoadVirtualFilesResumeEntriesByPathKeyBatchAsync(
            string syncPairId,
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken)
        {
            await using SyncStateDbContext context = contextFactory.Create();
            return await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId && keys.Contains(entry.RelativePathKey))
                .OrderBy(entry => entry.RelativePathKey)
                .Select(entry => new SyncVirtualFilesResumeEntry(
                    entry.RelativePath,
                    entry.Kind,
                    entry.RemoteNodeId,
                    entry.RemoteFileId,
                    entry.RemoteContentHash,
                    entry.RemoteETag,
                    entry.PlaceholderHydrationState,
                    entry.PlaceholderIdentity != null && entry.PlaceholderIdentity.Length > 0))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
