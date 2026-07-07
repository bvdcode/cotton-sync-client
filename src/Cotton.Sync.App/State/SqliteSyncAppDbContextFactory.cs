// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Data.Common;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.App.State
{
    internal class SqliteSyncAppDbContextFactory
    {
        private const int BusyTimeoutSeconds = 5;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> DatabaseGates = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _databasePath;
        private readonly string _databaseKey;

        public SqliteSyncAppDbContextFactory(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            _databasePath = databasePath;
            _databaseKey = Path.GetFullPath(databasePath);
        }

        public SyncAppDbContext Create()
        {
            string connectionString = CreateConnectionString();
            DbContextOptions<SyncAppDbContext> options = new DbContextOptionsBuilder<SyncAppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new SyncAppDbContext(options);
        }

        internal string CreateConnectionString()
        {
            return new DbConnectionStringBuilder
            {
                ["Data Source"] = _databasePath,
                ["Default Timeout"] = BusyTimeoutSeconds,
                ["Pooling"] = false,
            }.ToString();
        }

        public void EnsureDirectoryExists()
        {
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        public async Task MigrateAsync(CancellationToken cancellationToken)
        {
            EnsureDirectoryExists();
            SemaphoreSlim gate = GetDatabaseGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncAppDbContext context = Create();
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task ExecuteWriteAsync(
            Func<SyncAppDbContext, CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            EnsureDirectoryExists();
            SemaphoreSlim gate = GetDatabaseGate();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using SyncAppDbContext context = Create();
                await operation(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private SemaphoreSlim GetDatabaseGate()
        {
            return DatabaseGates.GetOrAdd(_databaseKey, static _ => new SemaphoreSlim(1, 1));
        }
    }
}
