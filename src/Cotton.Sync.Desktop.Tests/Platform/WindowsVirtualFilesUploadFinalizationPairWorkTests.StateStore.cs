// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public void UpsertDirectory(SyncPairSettings syncPair, string relativePath, Guid remoteNodeId)
            {
                _entries[CreateKey(syncPair.Id.ToString("D"), relativePath)] = new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = remoteNodeId,
                    SyncedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                };
            }

            public void UpsertFile(SyncPairSettings syncPair, string relativePath)
            {
                _entries[CreateKey(syncPair.Id.ToString("D"), relativePath)] = new SyncStateEntry
                {
                    SyncPairId = syncPair.Id.ToString("D"),
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.File,
                    RemoteNodeId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    RemoteFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    RemoteFileManifestId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    RemoteOriginalNodeFileId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    RemoteContentHash = "uploaded-content-hash",
                    RemoteETag = "uploaded-etag",
                    RemoteSizeBytes = 123,
                    SyncedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                };
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<SyncStateEntry>>(
                    _entries.Values.Where(entry => entry.SyncPairId == syncPairId).ToArray());
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (SyncStateEntry entry in _entries.Values.Where(entry => entry.SyncPairId == syncPairId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                    await Task.Yield();
                }
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                _entries.TryGetValue(CreateKey(syncPairId, relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                _entries.Remove(CreateKey(syncPairId, relativePath));
                return Task.CompletedTask;
            }

            public async Task DeleteByPathPrefixAsync(string syncPairId, string relativePathPrefix, CancellationToken cancellationToken = default)
            {
                string key = SyncPath.ToKey(relativePathPrefix);
                IReadOnlyList<SyncStateEntry> entries = await LoadPairAsync(syncPairId, cancellationToken);
                foreach (SyncStateEntry entry in entries.Where(entry =>
                             SyncPath.ToKey(entry.RelativePath) == key
                             || SyncPath.ToKey(entry.RelativePath).StartsWith(key + "/", StringComparison.Ordinal)))
                {
                    await DeleteAsync(syncPairId, entry.RelativePath, cancellationToken);
                }
            }
            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries.Values
                             .Where(entry => entry.SyncPairId == syncPairId)
                             .Select(entry => CreateKey(entry.SyncPairId, entry.RelativePath))
                             .ToArray())
                {
                    _entries.Remove(key);
                }

                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                _ = DeletePairAsync(syncPairId, cancellationToken);
                foreach (SyncStateEntry entry in entries)
                {
                    _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
                }

                return Task.CompletedTask;
            }

            private static string CreateKey(string syncPairId, string relativePath)
            {
                return syncPairId + "|" + SyncPath.ToKey(relativePath);
            }
        }

    }
}
