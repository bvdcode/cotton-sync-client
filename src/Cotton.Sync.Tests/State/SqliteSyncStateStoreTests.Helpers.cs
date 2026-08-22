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
            DbConnectionStringBuilder connectionString = new DbConnectionStringBuilder
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

        private static async Task<SqlitePageUsage> ReadPageUsageAsync(string databasePath)
        {
            DbConnectionStringBuilder connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using SyncStateDbContext context = new SyncStateDbContext(options);
            await context.Database.OpenConnectionAsync();
            try
            {
                DbConnection connection = context.Database.GetDbConnection();
                long pageCount = await ExecuteScalarLongAsync(connection, "PRAGMA page_count;");
                long freelistCount = await ExecuteScalarLongAsync(connection, "PRAGMA freelist_count;");
                long pageSize = await ExecuteScalarLongAsync(connection, "PRAGMA page_size;");
                return new SqlitePageUsage(pageCount, freelistCount, pageSize);
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }

        private static async Task<long> ExecuteScalarLongAsync(DbConnection connection, string commandText)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            object? result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<string> ReadIndexColumnsAsync(string databasePath, string indexName)
        {
            DbConnectionStringBuilder connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using SyncStateDbContext context = new SyncStateDbContext(options);
            await context.Database.OpenConnectionAsync();
            try
            {
                DbConnection connection = context.Database.GetDbConnection();
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = "SELECT group_concat(name, ',') FROM pragma_index_info($indexName) ORDER BY seqno;";
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "$indexName";
                parameter.Value = indexName;
                command.Parameters.Add(parameter);
                object? result = await command.ExecuteScalarAsync();
                return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }

        private record SqlitePageUsage(long PageCount, long FreelistCount, long PageSize)
        {
            public long FileBytes => PageCount * PageSize;

            public long FreelistBytes => FreelistCount * PageSize;
        }
    }
}
