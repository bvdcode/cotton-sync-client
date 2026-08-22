// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SyncStateStoreReader(
        SyncStateDbContextFactory contextFactory,
        SyncStateStoreInitializer initializer)
    {
        private const string SqlLikeEscapeCharacter = "\\";
        private const char SqlLikeEscapeCharacterValue = '\\';

        public async Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
            string syncPairId,
            CancellationToken cancellationToken)
        {
            List<SyncStateEntry> entries = [];
            await foreach (SyncStateEntry entry in LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                entries.Add(entry);
            }

            return entries;
        }

        public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
            string syncPairId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
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

        public async IAsyncEnumerable<SyncStateEntry> LoadPairDirectoryEntriesAsync(
            string syncPairId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
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

        public async IAsyncEnumerable<SyncStateEntry> LoadDirectoryEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePathPrefix);
            string prefixKey = SyncPath.ToKey(relativePathPrefix);
            string childPattern = CreateChildPathLikePattern(prefixKey);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
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

        public async IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathPrefixAsync(
            string syncPairId,
            string relativePathPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePathPrefix);
            string prefixKey = SyncPath.ToKey(relativePathPrefix);
            string childPattern = CreateChildPathLikePattern(prefixKey);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
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

        public async Task<DateTime?> GetPairLastSyncedAtUtcAsync(
            string syncPairId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
            DateTime? lastSyncedAtUtc = await context.SyncEntries
                .AsNoTracking()
                .Where(entry => entry.SyncPairId == syncPairId)
                .Select(entry => (DateTime?)entry.SyncedAt)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false);
            return SyncStateEntityMapper.ToUtc(lastSyncedAtUtc);
        }

        public async Task<SyncChangeCursor> GetChangeCursorAsync(
            string syncPairId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
            SyncChangeCursorEntity? entity = await context.SyncChangeCursors
                .AsNoTracking()
                .SingleOrDefaultAsync(cursor => cursor.SyncPairId == syncPairId, cancellationToken)
                .ConfigureAwait(false);
            return entity is null
                ? SyncStateEntityMapper.CreateDefaultCursor(syncPairId)
                : SyncStateEntityMapper.ToModel(entity);
        }

        public async Task<SyncStateEntry?> GetAsync(
            string syncPairId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            string key = SyncPath.ToKey(relativePath);
            await initializer.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SyncStateDbContext context = contextFactory.Create();
            SyncStateEntity? entity = await context.SyncEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.SyncPairId == syncPairId && entry.RelativePathKey == key,
                    cancellationToken)
                .ConfigureAwait(false);
            return entity is null ? null : SyncStateEntityMapper.ToModel(entity);
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
    }
}
