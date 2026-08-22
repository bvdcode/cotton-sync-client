// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly List<SyncStateEntry> _entries;

            public FakeSyncStateStore(params SyncStateEntry[] entries)
            {
                _entries = [.. entries];
                Cursor = new SyncChangeCursor
                {
                    HasCompletedFullReconcile = true,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
            }

            public SyncChangeCursor Cursor { get; set; }

            public List<SyncChangeCursor> SavedCursors { get; } = [];

            public int LoadPairEntriesCallCount { get; private set; }

            public int RemoteIdLookupCallCount { get; private set; }

            public int PathPrefixLookupCallCount { get; private set; }

            public IReadOnlyList<Guid> LastRemoteNodeIds { get; private set; } = [];

            public IReadOnlyList<Guid> LastRemoteFileIds { get; private set; } = [];

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<SyncStateEntry> entries = _entries
                    .Where(entry => entry.SyncPairId == syncPairId)
                    .ToArray();
                return Task.FromResult(entries);
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCallCount++;
                await Task.Yield();
                foreach (SyncStateEntry entry in _entries.Where(entry => entry.SyncPairId == syncPairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                }
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByRemoteIdsAsync(
                string syncPairId,
                IEnumerable<Guid> remoteNodeIds,
                IEnumerable<Guid> remoteFileIds,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                RemoteIdLookupCallCount++;
                HashSet<Guid> nodeIds = remoteNodeIds.Where(static id => id != Guid.Empty).ToHashSet();
                HashSet<Guid> fileIds = remoteFileIds.Where(static id => id != Guid.Empty).ToHashSet();
                LastRemoteNodeIds = nodeIds.ToArray();
                LastRemoteFileIds = fileIds.ToArray();
                await Task.Yield();
                foreach (SyncStateEntry entry in _entries.Where(entry => entry.SyncPairId == syncPairId))
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

            public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathPrefixAsync(
                string syncPairId,
                string relativePathPrefix,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                PathPrefixLookupCallCount++;
                string prefixKey = SyncPath.ToKey(relativePathPrefix);
                string childPrefix = prefixKey + "/";
                await Task.Yield();
                foreach (SyncStateEntry entry in _entries.Where(entry => entry.SyncPairId == syncPairId))
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

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor
                {
                    SyncPairId = syncPairId,
                    LastCursor = Cursor.LastCursor,
                    CursorExpired = Cursor.CursorExpired,
                    EarliestAvailableCursor = Cursor.EarliestAvailableCursor,
                    HasCompletedFullReconcile = Cursor.HasCompletedFullReconcile,
                    UpdatedAtUtc = Cursor.UpdatedAtUtc,
                });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                SyncStateEntry? entry = _entries.SingleOrDefault(item =>
                    item.SyncPairId == syncPairId
                    && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item =>
                    item.SyncPairId == entry.SyncPairId
                    && string.Equals(item.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
                _entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                Cursor = new SyncChangeCursor
                {
                    SyncPairId = cursor.SyncPairId,
                    LastCursor = cursor.LastCursor,
                    CursorExpired = cursor.CursorExpired,
                    EarliestAvailableCursor = cursor.EarliestAvailableCursor,
                    HasCompletedFullReconcile = cursor.HasCompletedFullReconcile,
                    UpdatedAtUtc = cursor.UpdatedAtUtc,
                };
                SavedCursors.Add(Cursor);
                return Task.CompletedTask;
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item =>
                    item.SyncPairId == syncPairId
                    && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item => item.SyncPairId == syncPairId);
                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                _entries.RemoveAll(item => item.SyncPairId == syncPairId);
                _entries.AddRange(entries);
                return Task.CompletedTask;
            }
        }
    }
}
