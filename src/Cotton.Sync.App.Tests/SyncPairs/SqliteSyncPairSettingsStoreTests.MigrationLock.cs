// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.State;
using Cotton.Sync.App.SyncPairs;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cotton.Sync.App.Tests.SyncPairs
{
    public partial class SqliteSyncPairSettingsStoreTests
    {
        [Test]
        public async Task InitializeAsync_AlreadyMigratedDatabaseDoesNotAcquireMigrationLock()
        {
            SqliteSyncPairSettingsStore first = CreateStore();
            await first.InitializeAsync();
            SyncPairSettings pair = CreatePair("Documents", "/sync/Documents", "/Documents");
            await first.UpsertAsync(pair);
            SqliteSyncAppDbContextFactory factory = new(DatabasePath());
            await using SyncAppDbContext context = factory.Create();
            using IMigrationsDatabaseLock migrationLock = await context.GetService<IHistoryRepository>()
                .AcquireDatabaseLockAsync(CancellationToken.None);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            SqliteSyncPairSettingsStore reopened = CreateStore();

            await reopened.InitializeAsync(timeout.Token);
            SyncPairSettings? actual = await reopened.GetAsync(pair.Id, timeout.Token);

            Assert.That(actual?.Id, Is.EqualTo(pair.Id));
            Assert.That(actual?.IsEnabled, Is.True);
        }
    }
}
