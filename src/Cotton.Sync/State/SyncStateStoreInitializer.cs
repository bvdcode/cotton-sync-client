// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.State
{
    internal class SyncStateStoreInitializer(SyncStateDbContextFactory contextFactory)
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationGates = new(StringComparer.OrdinalIgnoreCase);
        private bool _initialized;

        public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            contextFactory.EnsureDirectoryExists();
            string fullPath = Path.GetFullPath(contextFactory.DatabasePath);
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

                await using SyncStateDbContext context = contextFactory.Create();
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
