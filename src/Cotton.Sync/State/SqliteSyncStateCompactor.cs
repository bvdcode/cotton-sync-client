// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SqliteSyncStateCompactor
    {
        private const long MinimumFreelistBytes = 4L * 1024 * 1024;
        private const double MinimumFreelistRatio = 0.25d;
        private const int MaintenanceTimeoutSeconds = 120;
        private const int CopyBatchSize = 500;

        private readonly SyncStateDbContextFactory _contextFactory;

        public SqliteSyncStateCompactor(SyncStateDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task TryCompactAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CompactAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning(
                    "Failed to compact sync state database after deleting a sync pair. {0}",
                    exception);
            }
        }

        private async Task CompactAsync(CancellationToken cancellationToken)
        {
            string databasePath = Path.GetFullPath(_contextFactory.DatabasePath);
            SqlitePageUsage usage = await SqliteDatabaseHeaderReader
                .ReadAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);
            if (!ShouldCompact(usage))
            {
                return;
            }

            string compactedPath = databasePath + ".compact-" + Guid.NewGuid().ToString("N");
            try
            {
                await CopyCurrentStateAsync(compactedPath, cancellationToken).ConfigureAwait(false);
                File.Move(compactedPath, databasePath, overwrite: true);
            }
            finally
            {
                File.Delete(compactedPath);
                File.Delete(compactedPath + "-shm");
                File.Delete(compactedPath + "-wal");
            }
        }

        private async Task CopyCurrentStateAsync(string compactedPath, CancellationToken cancellationToken)
        {
            SyncStateDbContextFactory compactedFactory = new(compactedPath, MaintenanceTimeoutSeconds);
            await using (SyncStateDbContext destination = compactedFactory.Create())
            {
                await destination.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SyncStateMaintenanceDbContext source = _contextFactory.CreateMaintenance();
            await using SyncStateMaintenanceDbContext compacted = compactedFactory.CreateMaintenance();
            await CopyEntriesAsync(source, compacted, cancellationToken).ConfigureAwait(false);
            await CopyCursorsAsync(source, compacted, cancellationToken).ConfigureAwait(false);
        }

        private static async Task CopyEntriesAsync(
            SyncStateMaintenanceDbContext source,
            SyncStateMaintenanceDbContext destination,
            CancellationToken cancellationToken)
        {
            List<SyncStateEntity> batch = new(CopyBatchSize);
            await foreach (SyncStateEntity entity in source.SyncEntries
                .AsNoTracking()
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                batch.Add(entity);
                if (batch.Count == CopyBatchSize)
                {
                    await SaveBatchAsync(destination, batch, cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveBatchAsync(destination, batch, cancellationToken).ConfigureAwait(false);
        }

        private static async Task CopyCursorsAsync(
            SyncStateMaintenanceDbContext source,
            SyncStateMaintenanceDbContext destination,
            CancellationToken cancellationToken)
        {
            List<SyncChangeCursorEntity> batch = new(CopyBatchSize);
            await foreach (SyncChangeCursorEntity entity in source.SyncChangeCursors
                .AsNoTracking()
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                batch.Add(entity);
                if (batch.Count == CopyBatchSize)
                {
                    await SaveBatchAsync(destination, batch, cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveBatchAsync(destination, batch, cancellationToken).ConfigureAwait(false);
        }

        private static async Task SaveBatchAsync<TEntity>(
            SyncStateMaintenanceDbContext destination,
            List<TEntity> batch,
            CancellationToken cancellationToken)
            where TEntity : class
        {
            if (batch.Count == 0)
            {
                return;
            }

            destination.AddRange(batch);
            await destination.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            destination.ChangeTracker.Clear();
            batch.Clear();
        }

        private static bool ShouldCompact(SqlitePageUsage usage)
        {
            if (usage.PageCount <= 0 || usage.FreelistCount <= 0 || usage.PageSize <= 0)
            {
                return false;
            }

            long freelistBytes = usage.FreelistCount > long.MaxValue / usage.PageSize
                ? long.MaxValue
                : usage.FreelistCount * usage.PageSize;
            double ratio = usage.FreelistCount / (double)usage.PageCount;
            return freelistBytes >= MinimumFreelistBytes && ratio >= MinimumFreelistRatio;
        }
    }
}
