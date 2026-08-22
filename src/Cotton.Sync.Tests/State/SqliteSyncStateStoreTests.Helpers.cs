// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Data.Common;
using Cotton.Sync.State;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
    {
        private static async Task CreateInitialStateDatabaseAsync(string databasePath)
        {
            await CreateMigratedStateDatabaseAsync(databasePath, "20260602175534_InitialSyncState");
        }

        private static async Task CreateLocalSizeStateDatabaseAsync(string databasePath)
        {
            await CreateMigratedStateDatabaseAsync(databasePath, "20260606223759_AddLocalSizeToSyncState");
        }

        private static async Task CreateMigratedStateDatabaseAsync(string databasePath, string migration)
        {
            string connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using SyncStateDbContext context = new SyncStateDbContext(options);
            await context.Database.MigrateAsync(migration);
        }

        private static string ReadDirectoryRepairIndexColumns(string databasePath)
        {
            string connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            using SyncStateDbContext context = new SyncStateDbContext(options);
            Microsoft.EntityFrameworkCore.Metadata.IIndex index = context.Model
                .FindEntityType(typeof(SyncStateEntity))!
                .GetIndexes()
                .Single(candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(
                    [nameof(SyncStateEntity.SyncPairId), nameof(SyncStateEntity.Kind), nameof(SyncStateEntity.RelativePathKey)]));
            return string.Join(',', index.Properties.Select(property => property.GetColumnName()));
        }
    }
}
