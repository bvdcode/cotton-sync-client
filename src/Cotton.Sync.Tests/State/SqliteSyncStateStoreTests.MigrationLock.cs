// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
    {
        [Test]
        public async Task InitializeAsync_AlreadyMigratedDatabaseDoesNotAcquireMigrationLock()
        {
            SqliteSyncStateStore first = CreateStore();
            await first.InitializeAsync();
            await first.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 42,
                HasCompletedFullReconcile = true,
            });
            SyncStateDbContextFactory factory = new(DatabasePath(), 5);
            await using SyncStateDbContext context = factory.Create();
            using IMigrationsDatabaseLock migrationLock = await context.GetService<IHistoryRepository>()
                .AcquireDatabaseLockAsync(CancellationToken.None);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            SqliteSyncStateStore reopened = CreateStore();

            await reopened.InitializeAsync(timeout.Token);
            SyncChangeCursor cursor = await reopened.GetChangeCursorAsync("pair-a", timeout.Token);

            Assert.That(cursor.LastCursor, Is.EqualTo(42));
            Assert.That(cursor.HasCompletedFullReconcile, Is.True);
        }
    }
}
