// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    /// <summary>
    /// Persists sync baselines in a SQLite database through Entity Framework Core.
    /// </summary>
    public class SqliteSyncStateStore : ISyncStateStore
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationGates = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);
        private const int DefaultSqliteTimeoutSeconds = 30;
        private const int DefaultPathKeyLookupBatchSize = 500;

        private readonly string _databasePath;
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteSyncStateStore" /> class.
        /// </summary>
        public SqliteSyncStateStore(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            _databasePath = databasePath;
        }

        /// <inheritdoc />
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(string syncPairId, CancellationToken cancellationToken = default)
        {
            var entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                entries.Add(entry);
            }

            return entries;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
            string syncPairId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            IAsyncEnumerable<SyncStateEntity> entities = context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId)
                .OrderBy(entry => entry.RelativePathKey)
                .AsAsyncEnumerable();
            await foreach (SyncStateEntity entity in entities.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return ToModel(entity);
            }
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetPairLastSyncedAtUtcAsync(string syncPairId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            DateTime? lastSyncedAtUtc = await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId)
                .Select(entry => (DateTime?)entry.SyncedAtUtc)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false);
            return ToUtc(lastSyncedAtUtc);
        }

        /// <inheritdoc />
        public async Task<SyncChangeCursor> GetChangeCursorAsync(string syncPairId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            SyncChangeCursorEntity? entity = await context.SyncChangeCursors
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    cursor => cursor.SyncPairId == syncPairId,
                    cancellationToken)
                .ConfigureAwait(false);
            return entity is null ? CreateDefaultCursor(syncPairId) : ToModel(entity);
        }

        /// <inheritdoc />
        public async Task<SyncStateEntry?> GetAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            string key = SyncPath.ToKey(relativePath);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            SyncStateEntity? entity = await context.SyncEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.SyncPairId == syncPairId && entry.RelativePathKey == key,
                    cancellationToken)
                .ConfigureAwait(false);
            return entity is null ? null : ToModel(entity);
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
                await using SyncStateDbContext context = CreateContext();
                SyncStateEntity? entity = await context.SyncEntries
                    .SingleOrDefaultAsync(
                        existing => existing.SyncPairId == entry.SyncPairId && existing.RelativePathKey == key,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    entity = new SyncStateEntity
                    {
                        SyncPairId = entry.SyncPairId,
                        RelativePathKey = key,
                    };
                    context.SyncEntries.Add(entity);
                }

                UpdateEntity(entity, entry, key);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
            string syncPairId,
            IEnumerable<string> relativePathKeys,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentNullException.ThrowIfNull(relativePathKeys);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var keyBatch = new List<string>(DefaultPathKeyLookupBatchSize);
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

                if (keyBatch.Count >= DefaultPathKeyLookupBatchSize)
                {
                    foreach (SyncStateEntry entry in await LoadEntriesByPathKeyBatchAsync(syncPairId, keyBatch, cancellationToken).ConfigureAwait(false))
                    {
                        yield return entry;
                    }

                    keyBatch.Clear();
                }
            }

            if (keyBatch.Count > 0)
            {
                foreach (SyncStateEntry entry in await LoadEntriesByPathKeyBatchAsync(syncPairId, keyBatch, cancellationToken).ConfigureAwait(false))
                {
                    yield return entry;
                }
            }
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

            var normalizedEntries = new List<(SyncStateEntry Entry, string Key)>(entries.Count);
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
                await using SyncStateDbContext context = CreateContext();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
                            entity = new SyncStateEntity
                            {
                                SyncPairId = syncPairId,
                                RelativePathKey = key,
                            };
                            context.SyncEntries.Add(entity);
                        }

                        UpdateEntity(entity, entry, key);
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
            ValidateCursor(cursor);

            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            SemaphoreSlim gate = GetWriteGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncStateDbContext context = CreateContext();
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

                UpdateEntity(entity, cursor);
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
                await using SyncStateDbContext context = CreateContext();
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
                await using SyncStateDbContext context = CreateContext();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
                await using SyncStateDbContext context = CreateContext();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await context.SyncEntries
                    .Where(entry => entry.SyncPairId == syncPairId)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (SyncStateEntry entry in entries)
                {
                    entry.SyncPairId = syncPairId;
                    entry.RelativePath = SyncPath.Normalize(entry.RelativePath);
                    string key = SyncPath.ToKey(entry.RelativePath);
                    var entity = new SyncStateEntity
                    {
                        SyncPairId = syncPairId,
                        RelativePathKey = key,
                    };
                    UpdateEntity(entity, entry, key);
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
            EnsureDirectoryExists();
            string fullPath = Path.GetFullPath(_databasePath);
            if (_initialized && File.Exists(fullPath))
            {
                return;
            }

            SemaphoreSlim gate = MigrationGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized && File.Exists(fullPath))
                {
                    return;
                }

                await using SyncStateDbContext context = CreateContext();
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            finally
            {
                gate.Release();
            }
        }

        private static void UpdateEntity(SyncStateEntity entity, SyncStateEntry entry, string key)
        {
            if (entry.LocalSizeBytes.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(entry.LocalSizeBytes.Value);
            }

            if (entry.RemoteSizeBytes.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(entry.RemoteSizeBytes.Value);
            }

            entity.SyncPairId = entry.SyncPairId;
            entity.RelativePathKey = key;
            entity.RelativePath = SyncPath.Normalize(entry.RelativePath);
            entity.Kind = entry.Kind;
            entity.LocalContentHash = NormalizeNullable(entry.LocalContentHash);
            entity.LocalLastWriteUtc = ToUtc(entry.LocalLastWriteUtc);
            entity.LocalSizeBytes = entry.LocalSizeBytes;
            entity.RemoteSizeBytes = entry.RemoteSizeBytes;
            entity.RemoteNodeId = entry.RemoteNodeId;
            entity.RemoteFileId = entry.RemoteFileId;
            entity.RemoteContentHash = NormalizeNullable(entry.RemoteContentHash);
            entity.RemoteETag = NormalizeNullable(entry.RemoteETag);
            entity.PlaceholderIdentity = Clone(entry.PlaceholderIdentity);
            entity.PlaceholderHydrationState = entry.PlaceholderHydrationState;
            entity.SyncedAtUtc = ToUtc(entry.SyncedAtUtc) ?? DateTime.UtcNow;
        }

        private static SyncStateEntry ToModel(SyncStateEntity entity)
        {
            return new SyncStateEntry
            {
                SyncPairId = entity.SyncPairId,
                RelativePath = entity.RelativePath,
                Kind = entity.Kind,
                LocalContentHash = entity.LocalContentHash,
                LocalLastWriteUtc = ToUtc(entity.LocalLastWriteUtc),
                LocalSizeBytes = entity.LocalSizeBytes,
                RemoteSizeBytes = entity.RemoteSizeBytes,
                RemoteNodeId = entity.RemoteNodeId,
                RemoteFileId = entity.RemoteFileId,
                RemoteContentHash = entity.RemoteContentHash,
                RemoteETag = entity.RemoteETag,
                PlaceholderIdentity = Clone(entity.PlaceholderIdentity),
                PlaceholderHydrationState = entity.PlaceholderHydrationState,
                SyncedAtUtc = ToUtc(entity.SyncedAtUtc) ?? DateTime.UtcNow,
            };
        }

        private static SyncChangeCursor CreateDefaultCursor(string syncPairId)
        {
            return new SyncChangeCursor
            {
                SyncPairId = syncPairId,
                LastCursor = 0,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static void UpdateEntity(SyncChangeCursorEntity entity, SyncChangeCursor cursor)
        {
            entity.SyncPairId = cursor.SyncPairId;
            entity.LastCursor = cursor.LastCursor;
            entity.CursorExpired = cursor.CursorExpired;
            entity.EarliestAvailableCursor = cursor.EarliestAvailableCursor;
            entity.UpdatedAtUtc = ToUtc(cursor.UpdatedAtUtc) ?? DateTime.UtcNow;
        }

        private static SyncChangeCursor ToModel(SyncChangeCursorEntity entity)
        {
            return new SyncChangeCursor
            {
                SyncPairId = entity.SyncPairId,
                LastCursor = entity.LastCursor,
                CursorExpired = entity.CursorExpired,
                EarliestAvailableCursor = entity.EarliestAvailableCursor,
                UpdatedAtUtc = ToUtc(entity.UpdatedAtUtc) ?? DateTime.UtcNow,
            };
        }

        private static void ValidateCursor(SyncChangeCursor cursor)
        {
            if (cursor.LastCursor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), cursor.LastCursor, "Change cursor cannot be negative.");
            }

            if (cursor.EarliestAvailableCursor < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cursor),
                    cursor.EarliestAvailableCursor,
                    "Earliest available cursor cannot be negative.");
            }
        }

        private SyncStateDbContext CreateContext()
        {
            var connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = _databasePath,
                ["Pooling"] = false,
                ["Default Timeout"] = DefaultSqliteTimeoutSeconds,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new SyncStateDbContext(options);
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadEntriesByPathKeyBatchAsync(
            string syncPairId,
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken)
        {
            await using SyncStateDbContext context = CreateContext();
            List<SyncStateEntity> entities = await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId && keys.Contains(entry.RelativePathKey))
                .OrderBy(entry => entry.RelativePathKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return entities.Select(ToModel).ToArray();
        }

        private SemaphoreSlim GetWriteGate()
        {
            return WriteGates.GetOrAdd(
                Path.GetFullPath(_databasePath),
                static _ => new SemaphoreSlim(1, 1));
        }

        private void EnsureDirectoryExists()
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string? NormalizeNullable(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static byte[]? Clone(byte[]? value)
        {
            return value is null ? null : (byte[])value.Clone();
        }

        private static DateTime? ToUtc(DateTime? value)
        {
            return value?.Kind switch
            {
                null => null,
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
                _ => value.Value.ToUniversalTime(),
            };
        }
    }
}
