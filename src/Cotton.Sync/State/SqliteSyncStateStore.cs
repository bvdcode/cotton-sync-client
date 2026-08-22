// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cotton.Sync.State
{
    /// <summary>
    /// Persists sync baselines in a SQLite database through Entity Framework Core.
    /// </summary>
    public class SqliteSyncStateStore : ISyncStateStore, IVirtualFilesResumeStateStore
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);
        private const int DefaultSqliteTimeoutSeconds = 30;

        private readonly SyncStateDbContextFactory _contextFactory;
        private readonly SyncStateStoreInitializer _initializer;
        private readonly SyncStateStoreReader _reader;
        private readonly SyncStateLookupReader _lookupReader;
        private readonly SqliteSyncStateDiagnosticsReader _diagnosticsReader;
        private readonly SqliteSyncStateCompactor _compactor;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteSyncStateStore" /> class.
        /// </summary>
        public SqliteSyncStateStore(string databasePath)
        {
            _contextFactory = new SyncStateDbContextFactory(databasePath, DefaultSqliteTimeoutSeconds);
            _initializer = new SyncStateStoreInitializer(_contextFactory);
            _reader = new SyncStateStoreReader(_contextFactory, _initializer);
            _lookupReader = new SyncStateLookupReader(_contextFactory, _initializer);
            _diagnosticsReader = new SqliteSyncStateDiagnosticsReader(_contextFactory, _initializer);
            _compactor = new SqliteSyncStateCompactor(_contextFactory);
        }

        /// <inheritdoc />
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
            string syncPairId,
            CancellationToken cancellationToken = default)
        {
            return await _reader.LoadPairAsync(syncPairId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
            string syncPairId,
            CancellationToken cancellationToken = default)
        {
            return _reader.LoadPairEntriesAsync(syncPairId, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncStateEntry> LoadPairDirectoryEntriesAsync(
            string syncPairId,
            CancellationToken cancellationToken = default)
        {
            return _reader.LoadPairDirectoryEntriesAsync(syncPairId, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncStateEntry> LoadDirectoryEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            CancellationToken cancellationToken = default)
        {
            return _reader.LoadDirectoryEntriesByPathPrefixAsync(
                syncPairId,
                relativePathPrefix,
                cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            CancellationToken cancellationToken = default)
        {
            return _reader.LoadEntriesByPathPrefixAsync(syncPairId, relativePathPrefix, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncStateEntry> LoadEntriesByRemoteIdsAsync(
            string syncPairId,
            IEnumerable<Guid> remoteNodeIds,
            IEnumerable<Guid> remoteFileIds,
            CancellationToken cancellationToken = default)
        {
            return _lookupReader.LoadEntriesByRemoteIdsAsync(
                syncPairId,
                remoteNodeIds,
                remoteFileIds,
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetPairLastSyncedAtUtcAsync(
            string syncPairId,
            CancellationToken cancellationToken = default)
        {
            return await _reader.GetPairLastSyncedAtUtcAsync(syncPairId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<SyncChangeCursor> GetChangeCursorAsync(
            string syncPairId,
            CancellationToken cancellationToken = default)
        {
            return await _reader.GetChangeCursorAsync(syncPairId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<SyncStateStoreDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            return await _diagnosticsReader.GetAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<SyncStateEntry?> GetAsync(
            string syncPairId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return await _reader.GetAsync(syncPairId, relativePath, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.SyncPairId);
            entry.RelativePath = SyncPath.Normalize(entry.RelativePath);
            if (entry.SyncedAtUtc == default)
            {
                entry.SyncedAtUtc = DateTime.UtcNow;
            }

            string key = SyncPath.ToKey(entry.RelativePath);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncStateDbContext context = _contextFactory.Create();
                SyncStateEntity? entity = await context.SyncEntries
                    .SingleOrDefaultAsync(
                        existing => existing.SyncPairId == entry.SyncPairId && existing.RelativePathKey == key,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    entity = new SyncStateEntity(Guid.CreateVersion7())
                    {
                        SyncPairId = entry.SyncPairId,
                        RelativePathKey = key,
                    };
                    context.SyncEntries.Add(entity);
                }

                SyncStateEntityMapper.Update(entity, entry, key);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            CancellationToken cancellationToken = default)
        {
            return _lookupReader.LoadEntriesByPathKeysAsync(syncPairId, relativePathKeys, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<SyncVirtualFilesResumeEntry> LoadVirtualFilesResumeEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            CancellationToken cancellationToken = default)
        {
            return _lookupReader.LoadVirtualFilesResumeEntriesByPathKeysAsync(
                syncPairId,
                relativePathKeys,
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task UpsertManyAsync(
            IReadOnlyCollection<SyncStateEntry> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);
            if (entries.Count == 0)
            {
                return;
            }

            List<(SyncStateEntry Entry, string Key)> normalizedEntries = new List<(SyncStateEntry Entry, string Key)>(entries.Count);
            foreach (SyncStateEntry entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                ArgumentException.ThrowIfNullOrWhiteSpace(entry.SyncPairId);
                entry.RelativePath = SyncPath.Normalize(entry.RelativePath);
                if (entry.SyncedAtUtc == default)
                {
                    entry.SyncedAtUtc = DateTime.UtcNow;
                }

                normalizedEntries.Add((entry, SyncPath.ToKey(entry.RelativePath)));
            }

            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncStateDbContext context = _contextFactory.Create();
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                context.ChangeTracker.AutoDetectChangesEnabled = false;
                foreach (IGrouping<string, (SyncStateEntry Entry, string Key)> group in normalizedEntries.GroupBy(item => item.Entry.SyncPairId))
                {
                    string syncPairId = group.Key;
                    Dictionary<string, (SyncStateEntry Entry, string Key)> entriesByKey = group
                        .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(item => item.Key, item => item.Last(), StringComparer.OrdinalIgnoreCase);
                    string[] keys = entriesByKey.Keys.ToArray();
                    Dictionary<string, SyncStateEntity> existingByKey = await context.SyncEntries
                        .Where(entry => entry.SyncPairId == syncPairId && keys.Contains(entry.RelativePathKey))
                        .ToDictionaryAsync(entry => entry.RelativePathKey, StringComparer.OrdinalIgnoreCase, cancellationToken)
                        .ConfigureAwait(false);

                    foreach ((string key, (SyncStateEntry entry, _)) in entriesByKey)
                    {
                        if (!existingByKey.TryGetValue(key, out SyncStateEntity? entity))
                        {
                            entity = new SyncStateEntity(Guid.CreateVersion7())
                            {
                                SyncPairId = syncPairId,
                                RelativePathKey = key,
                            };
                            context.SyncEntries.Add(entity);
                        }

                        SyncStateEntityMapper.Update(entity, entry, key);
                    }
                }

                context.ChangeTracker.DetectChanges();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cursor);
            ArgumentException.ThrowIfNullOrWhiteSpace(cursor.SyncPairId);
            SyncStateEntityMapper.Validate(cursor);

            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncStateDbContext context = _contextFactory.Create();
                SyncChangeCursorEntity? entity = await context.SyncChangeCursors
                    .SingleOrDefaultAsync(
                        existing => existing.SyncPairId == cursor.SyncPairId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    entity = new SyncChangeCursorEntity
                    {
                        SyncPairId = cursor.SyncPairId,
                    };
                    context.SyncChangeCursors.Add(entity);
                }

                SyncStateEntityMapper.Update(entity, cursor);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            string key = SyncPath.ToKey(relativePath);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncStateDbContext context = _contextFactory.Create();
                await context.SyncEntries
                    .Where(entry => entry.SyncPairId == syncPairId && entry.RelativePathKey == key)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (SyncStateDbContext context = _contextFactory.Create())
                {
                    await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    await context.SyncEntries
                        .Where(entry => entry.SyncPairId == syncPairId)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await context.SyncChangeCursors
                        .Where(cursor => cursor.SyncPairId == syncPairId)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                await _compactor.TryCompactAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task ReplacePairAsync(
            string syncPairId,
            IReadOnlyCollection<SyncStateEntry> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(entries);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncStateDbContext context = _contextFactory.Create();
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await context.SyncEntries
                    .Where(entry => entry.SyncPairId == syncPairId)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (SyncStateEntry entry in entries)
                {
                    entry.SyncPairId = syncPairId;
                    entry.RelativePath = SyncPath.Normalize(entry.RelativePath);
                    string key = SyncPath.ToKey(entry.RelativePath);
                    SyncStateEntity entity = new SyncStateEntity(Guid.CreateVersion7())
                    {
                        SyncPairId = syncPairId,
                        RelativePathKey = key,
                    };
                    SyncStateEntityMapper.Update(entity, entry, key);
                    context.SyncEntries.Add(entity);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            await _initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        private SemaphoreSlim GetWriteGate()
        {
            return WriteGates.GetOrAdd(
                Path.GetFullPath(_contextFactory.DatabasePath),
                static _ => new SemaphoreSlim(1, 1));
        }

    }
}
