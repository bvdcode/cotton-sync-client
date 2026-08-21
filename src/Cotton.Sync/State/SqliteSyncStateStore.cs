// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cotton.Sync.State
{
    /// <summary>
    /// Persists sync baselines in a SQLite database through Entity Framework Core.
    /// </summary>
    public class SqliteSyncStateStore : ISyncStateStore, IVirtualFilesResumeStateStore
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationGates = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);
        private const int DefaultSqliteTimeoutSeconds = 30;
        private const int MaintenanceSqliteTimeoutSeconds = 120;
        private const int DefaultPathKeyLookupBatchSize = 500;
        private const string SqlLikeEscapeCharacter = "\\";
        private const char SqlLikeEscapeCharacterValue = '\\';
        private const long MinimumFreelistBytesForVacuum = 4L * 1024 * 1024;
        private const double MinimumFreelistRatioForVacuum = 0.25d;

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
                yield return SyncStateEntityMapper.ToModel(entity);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SyncStateEntry> LoadPairDirectoryEntriesAsync(
            string syncPairId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            IAsyncEnumerable<SyncStateEntity> entities = context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId && entry.Kind == SyncEntryKind.Directory)
                .OrderBy(entry => entry.RelativePathKey)
                .AsAsyncEnumerable();
            await foreach (SyncStateEntity entity in entities.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return SyncStateEntityMapper.ToModel(entity);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SyncStateEntry> LoadDirectoryEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePathPrefix);
            string prefixKey = SyncPath.ToKey(relativePathPrefix);
            string childPattern = CreateChildPathLikePattern(prefixKey);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            SyncStateEntity? exactEntry = await context.SyncEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.SyncPairId == syncPairId
                        && entry.Kind == SyncEntryKind.Directory
                        && entry.RelativePathKey == prefixKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exactEntry is not null)
            {
                yield return SyncStateEntityMapper.ToModel(exactEntry);
            }

            IAsyncEnumerable<SyncStateEntity> entities = context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId
                    && entry.Kind == SyncEntryKind.Directory
                    && EF.Functions.Like(entry.RelativePathKey, childPattern, SqlLikeEscapeCharacter))
                .OrderBy(entry => entry.RelativePathKey)
                .AsAsyncEnumerable();
            await foreach (SyncStateEntity entity in entities.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return SyncStateEntityMapper.ToModel(entity);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePathPrefix);
            string prefixKey = SyncPath.ToKey(relativePathPrefix);
            string childPattern = CreateChildPathLikePattern(prefixKey);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = CreateContext();
            SyncStateEntity? exactEntry = await context.SyncEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.SyncPairId == syncPairId && entry.RelativePathKey == prefixKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exactEntry is not null)
            {
                yield return SyncStateEntityMapper.ToModel(exactEntry);
            }

            IAsyncEnumerable<SyncStateEntity> entities = context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId
                    && EF.Functions.Like(entry.RelativePathKey, childPattern, SqlLikeEscapeCharacter))
                .OrderBy(entry => entry.RelativePathKey)
                .AsAsyncEnumerable();
            await foreach (SyncStateEntity entity in entities.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return SyncStateEntityMapper.ToModel(entity);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByRemoteIdsAsync(
            string syncPairId,
            IEnumerable<Guid> remoteNodeIds,
            IEnumerable<Guid> remoteFileIds,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var yieldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IReadOnlyCollection<Guid> batch in CreateBatches(nodeIds, DefaultPathKeyLookupBatchSize))
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

            foreach (IReadOnlyCollection<Guid> batch in CreateBatches(fileIds, DefaultPathKeyLookupBatchSize))
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
            return SyncStateEntityMapper.ToUtc(lastSyncedAtUtc);
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
            return entity is null ? SyncStateEntityMapper.CreateDefaultCursor(syncPairId) : SyncStateEntityMapper.ToModel(entity);
        }

        public async Task<SyncStateStoreDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            string fullPath = Path.GetFullPath(_databasePath);
            await using SyncStateDbContext context = CreateContext();
            long syncEntryCount = await context.SyncEntries
                .LongCountAsync(cancellationToken)
                .ConfigureAwait(false);
            long syncChangeCursorCount = await context.SyncChangeCursors
                .LongCountAsync(cancellationToken)
                .ConfigureAwait(false);
            SqlitePageUsage pageUsage = await ReadPageUsageAsync(context, cancellationToken)
                .ConfigureAwait(false);
            long fileSizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            return new SyncStateStoreDiagnostics(
                fileSizeBytes,
                pageUsage.PageCount,
                pageUsage.FreelistCount,
                pageUsage.PageSize,
                syncEntryCount,
                syncChangeCursorCount);
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
            return entity is null ? null : SyncStateEntityMapper.ToModel(entity);
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

                SyncStateEntityMapper.Update(entity, entry, key);
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
        public async IAsyncEnumerable<SyncVirtualFilesResumeEntry> LoadVirtualFilesResumeEntriesByPathKeysAsync(
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
                    foreach (SyncVirtualFilesResumeEntry entry in await LoadVirtualFilesResumeEntriesByPathKeyBatchAsync(syncPairId, keyBatch, cancellationToken).ConfigureAwait(false))
                    {
                        yield return entry;
                    }

                    keyBatch.Clear();
                }
            }

            if (keyBatch.Count > 0)
            {
                foreach (SyncVirtualFilesResumeEntry entry in await LoadVirtualFilesResumeEntriesByPathKeyBatchAsync(syncPairId, keyBatch, cancellationToken).ConfigureAwait(false))
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
                            entity = new SyncStateEntity
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
                await using (SyncStateDbContext context = CreateContext())
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

                await TryCompactLargeFreelistAsync(cancellationToken).ConfigureAwait(false);
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
                    SyncStateEntity entity = new SyncStateEntity
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

        private SyncStateDbContext CreateContext()
        {
            string connectionString = new DbConnectionStringBuilder
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

        private static string CreateChildPathLikePattern(string prefixKey)
        {
            return EscapeSqlLikePattern(prefixKey + "/") + "%";
        }

        private static string EscapeSqlLikePattern(string value)
        {
            StringBuilder builder = new(value.Length);
            foreach (char character in value)
            {
                if (character == SqlLikeEscapeCharacterValue || character == '%' || character == '_')
                {
                    builder.Append(SqlLikeEscapeCharacterValue);
                }

                builder.Append(character);
            }

            return builder.ToString();
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
            await using SyncStateDbContext context = CreateContext();
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
            await using SyncStateDbContext context = CreateContext();
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
            await using SyncStateDbContext context = CreateContext();
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
            await using SyncStateDbContext context = CreateContext();
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

        private SemaphoreSlim GetWriteGate()
        {
            return WriteGates.GetOrAdd(
                Path.GetFullPath(_databasePath),
                static _ => new SemaphoreSlim(1, 1));
        }

        private async Task TryCompactLargeFreelistAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CompactLargeFreelistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning(
                    "Failed to compact sync state database after deleting a sync pair. {0}",
                    exception);
            }
        }

        private async Task CompactLargeFreelistAsync(CancellationToken cancellationToken)
        {
            await using SyncStateDbContext context = CreateContext();
            SqlitePageUsage pageUsage = await ReadPageUsageAsync(context, cancellationToken).ConfigureAwait(false);
            if (!ShouldVacuumFreelist(pageUsage.PageCount, pageUsage.FreelistCount, pageUsage.PageSize))
            {
                return;
            }

            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DbConnection connection = context.Database.GetDbConnection();
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = "VACUUM;";
                command.CommandTimeout = MaintenanceSqliteTimeoutSeconds;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        private static bool ShouldVacuumFreelist(long pageCount, long freelistCount, long pageSize)
        {
            if (pageCount <= 0 || freelistCount <= 0 || pageSize <= 0)
            {
                return false;
            }

            long freelistBytes = freelistCount > long.MaxValue / pageSize
                ? long.MaxValue
                : freelistCount * pageSize;
            double freelistRatio = freelistCount / (double)pageCount;
            return freelistBytes >= MinimumFreelistBytesForVacuum
                && freelistRatio >= MinimumFreelistRatioForVacuum;
        }

        private static async Task<SqlitePageUsage> ReadPageUsageAsync(
            SyncStateDbContext context,
            CancellationToken cancellationToken)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DbConnection connection = context.Database.GetDbConnection();
                long pageCount = await ExecuteScalarLongAsync(connection, "PRAGMA page_count;", cancellationToken)
                    .ConfigureAwait(false);
                long freelistCount = await ExecuteScalarLongAsync(connection, "PRAGMA freelist_count;", cancellationToken)
                    .ConfigureAwait(false);
                long pageSize = await ExecuteScalarLongAsync(connection, "PRAGMA page_size;", cancellationToken)
                    .ConfigureAwait(false);
                return new SqlitePageUsage(pageCount, freelistCount, pageSize);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        private static async Task<long> ExecuteScalarLongAsync(
            DbConnection connection,
            string commandText,
            CancellationToken cancellationToken)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            command.CommandTimeout = DefaultSqliteTimeoutSeconds;
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }

        private readonly record struct SqlitePageUsage(long PageCount, long FreelistCount, long PageSize);

        private void EnsureDirectoryExists()
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

    }
}
