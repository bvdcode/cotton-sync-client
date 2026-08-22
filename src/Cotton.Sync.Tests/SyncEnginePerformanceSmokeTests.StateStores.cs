// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Tests
{
    public partial class SyncEnginePerformanceSmokeTests
    {
        private class CountingScopedStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public CountingScopedStateStore(int logicalEntryCount, SyncStateEntry scopedEntry)
            {
                LogicalEntryCount = logicalEntryCount;
                _entries[SyncPath.ToKey(scopedEntry.RelativePath)] = scopedEntry;
            }

            public int LogicalEntryCount { get; }

            public int GetCalls { get; private set; }

            public int FullLoadCalls { get; private set; }

            public int UpsertCalls { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                FullLoadCalls++;
                throw new InvalidOperationException("1M logical hot-path smoke must not load the full state set.");
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                FullLoadCalls++;
                await Task.CompletedTask;
                yield break;
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(DateTime.UtcNow);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                GetCalls++;
                _entries.TryGetValue(SyncPath.ToKey(relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                UpsertCalls++;
                _entries[SyncPath.ToKey(entry.RelativePath)] = entry;
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.Remove(SyncPath.ToKey(relativePath));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                _entries.Clear();
                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                FullLoadCalls++;
                throw new InvalidOperationException("1M logical hot-path smoke must not replace full state.");
            }
        }

        private class CountingVirtualPlaceholderStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<int> _upsertManyEntryCounts = [];
            private readonly object _gate = new();
            private int _fileUpserts;
            private int _directoryUpserts;
            private int _remoteOnlyPlaceholderUpserts;
            private int _singleUpsertCalls;
            private int _upsertManyCalls;

            public CountingVirtualPlaceholderStateStore()
            {
            }

            public CountingVirtualPlaceholderStateStore(IEnumerable<SyncStateEntry> entries)
            {
                foreach (SyncStateEntry entry in entries)
                {
                    _entries[SyncPath.ToKey(entry.RelativePath)] = entry;
                }
            }

            public int FileUpserts => Volatile.Read(ref _fileUpserts);

            public int DirectoryUpserts => Volatile.Read(ref _directoryUpserts);

            public int RemoteOnlyPlaceholderUpserts => Volatile.Read(ref _remoteOnlyPlaceholderUpserts);

            public int SingleUpsertCalls => Volatile.Read(ref _singleUpsertCalls);

            public int UpsertManyCalls => Volatile.Read(ref _upsertManyCalls);

            public IReadOnlyList<int> UpsertManyEntryCounts
            {
                get
                {
                    lock (_gate)
                    {
                        return _upsertManyEntryCounts.ToArray();
                    }
                }
            }

            public int LoadPairEntriesCalls { get; private set; }

            public int LoadPairEntriesYieldCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<SyncStateEntry>>(_entries.Values.ToList());
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCalls++;
                await Task.CompletedTask.ConfigureAwait(false);
                foreach (SyncStateEntry entry in _entries.Values.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LoadPairEntriesYieldCount++;
                    yield return entry;
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
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.TryGetValue(SyncPath.ToKey(relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _singleUpsertCalls);
                UpsertEntry(entry);
                return Task.CompletedTask;
            }

            public Task UpsertManyAsync(
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _upsertManyCalls);
                lock (_gate)
                {
                    _upsertManyEntryCounts.Add(entries.Count);
                }

                foreach (SyncStateEntry entry in entries)
                {
                    UpsertEntry(entry);
                }

                return Task.CompletedTask;
            }

            private void UpsertEntry(SyncStateEntry entry)
            {
                _entries[SyncPath.ToKey(entry.RelativePath)] = entry;
                if (entry.Kind == SyncEntryKind.Directory)
                {
                    Interlocked.Increment(ref _directoryUpserts);
                    return;
                }

                if (entry.Kind == SyncEntryKind.File)
                {
                    Interlocked.Increment(ref _fileUpserts);
                    if (entry.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                        && entry.PlaceholderIdentity is { Length: > 0 }
                        && entry.LocalContentHash is null
                        && entry.LocalSizeBytes is null)
                    {
                        Interlocked.Increment(ref _remoteOnlyPlaceholderUpserts);
                    }
                }
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.Remove(SyncPath.ToKey(relativePath));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                _entries.Clear();
                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                _entries.Clear();
                foreach (SyncStateEntry entry in entries)
                {
                    _entries[SyncPath.ToKey(entry.RelativePath)] = entry;
                }

                return Task.CompletedTask;
            }
        }

    }
}
