// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SyncStateDbContextFactory
    {
        private readonly string _databasePath;
        private readonly int _commandTimeoutSeconds;

        public SyncStateDbContextFactory(string databasePath, int commandTimeoutSeconds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);
            _databasePath = databasePath;
            _commandTimeoutSeconds = commandTimeoutSeconds;
        }

        public string DatabasePath => _databasePath;

        public SyncStateDbContext Create()
        {
            string connectionString = CreateConnectionString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new SyncStateDbContext(options);
        }

        public SyncStateMaintenanceDbContext CreateMaintenance()
        {
            string connectionString = CreateConnectionString();
            DbContextOptions<SyncStateMaintenanceDbContext> options =
                new DbContextOptionsBuilder<SyncStateMaintenanceDbContext>()
                    .UseSqlite(connectionString)
                    .Options;
            return new SyncStateMaintenanceDbContext(options);
        }

        public void EnsureDirectoryExists()
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private string CreateConnectionString()
        {
            return new DbConnectionStringBuilder
            {
                ["Data Source"] = _databasePath,
                ["Pooling"] = false,
                ["Default Timeout"] = _commandTimeoutSeconds,
            }.ToString();
        }
    }
}
