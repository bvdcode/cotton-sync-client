// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Data.Common;
using Cotton.Sync.State;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
    {
        [Test]
        public async Task GetChangeCursorAsync_ReturnsDefaultCursorForNewPair()
        {
            SqliteSyncStateStore store = CreateStore();
            await store.InitializeAsync();

            SyncChangeCursor cursor = await store.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(cursor.SyncPairId, Is.EqualTo("pair-a"));
                Assert.That(cursor.LastCursor, Is.Zero);
                Assert.That(cursor.CursorExpired, Is.False);
                Assert.That(cursor.EarliestAvailableCursor, Is.Null);
                Assert.That(cursor.HasCompletedFullReconcile, Is.False);
                Assert.That(cursor.UpdatedAtUtc, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
            });
        }

        [Test]
        public async Task GetChangeCursorAsync_InitializesNewDatabaseWithoutExplicitInitialize()
        {
            SqliteSyncStateStore store = CreateStore();

            SyncChangeCursor cursor = await store.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(cursor.SyncPairId, Is.EqualTo("pair-a"));
                Assert.That(cursor.LastCursor, Is.Zero);
                Assert.That(cursor.CursorExpired, Is.False);
            });
        }

        [Test]
        public async Task SaveChangeCursorAsync_InitializesNewDatabaseWithoutExplicitInitialize()
        {
            string databasePath = DatabasePath();
            SqliteSyncStateStore first = new SqliteSyncStateStore(databasePath);

            await first.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 9,
            });

            SqliteSyncStateStore second = new SqliteSyncStateStore(databasePath);
            SyncChangeCursor cursor = await second.GetChangeCursorAsync("pair-a");

            Assert.That(cursor.LastCursor, Is.EqualTo(9));
        }

        [Test]
        public async Task InitializeAsync_MigratesInitialStateDatabaseToChangeCursors()
        {
            string databasePath = DatabasePath();
            await CreateInitialStateDatabaseAsync(databasePath);
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);

            await store.InitializeAsync();
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 5,
            });

            SyncChangeCursor cursor = await store.GetChangeCursorAsync("pair-a");

            Assert.That(cursor.LastCursor, Is.EqualTo(5));
        }

        [Test]
        public async Task InitializeAsync_CreatesDirectoryRepairLookupIndex()
        {
            string databasePath = DatabasePath();
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);

            await store.InitializeAsync();

            string indexColumns = await ReadIndexColumnsAsync(
                databasePath,
                "IX_sync_entries_sync_pair_id_kind_relative_path_key");

            Assert.That(indexColumns, Is.EqualTo("sync_pair_id,kind,relative_path_key"));
        }

        [Test]
        public async Task InitializeAsync_MigratesLocalSizeStateDatabaseToVirtualFileMetadata()
        {
            string databasePath = DatabasePath();
            await CreateLocalSizeStateDatabaseAsync(databasePath);
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
            Guid fileId = Guid.NewGuid();
            byte[] placeholderIdentity = [0x01, 0x02, 0x03, 0x04];

            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/placeholder.txt",
                Kind = SyncEntryKind.File,
                RemoteFileId = fileId,
                RemoteContentHash = "remote-hash",
                RemoteETag = "etag-1",
                RemoteSizeBytes = 12345,
                PlaceholderIdentity = placeholderIdentity,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
            });

            SyncStateEntry? entry = await store.GetAsync("pair-a", "docs/PLACEHOLDER.txt");

            Assert.Multiple(() =>
            {
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(fileId));
                Assert.That(entry.RemoteContentHash, Is.EqualTo("remote-hash"));
                Assert.That(entry.RemoteETag, Is.EqualTo("etag-1"));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(12345));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task UpsertAsync_InitializesNewDatabaseWithoutExplicitInitialize()
        {
            SqliteSyncStateStore store = CreateStore();

            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "ready.txt",
                Kind = SyncEntryKind.File,
                LocalContentHash = "hash",
            });

            SyncStateEntry? entry = await store.GetAsync("pair-a", "READY.txt");

            Assert.That(entry?.LocalContentHash, Is.EqualTo("hash"));
        }

        [Test]
        public async Task SaveChangeCursorAsync_RoundtripsAndPersistsAfterReopen()
        {
            string databasePath = DatabasePath();
            SqliteSyncStateStore first = new SqliteSyncStateStore(databasePath);
            await first.InitializeAsync();
            DateTime updatedAt = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
            await first.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 42,
                CursorExpired = true,
                EarliestAvailableCursor = 40,
                HasCompletedFullReconcile = true,
                UpdatedAtUtc = updatedAt,
            });

            SqliteSyncStateStore second = new SqliteSyncStateStore(databasePath);
            await second.InitializeAsync();
            SyncChangeCursor cursor = await second.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(cursor.SyncPairId, Is.EqualTo("pair-a"));
                Assert.That(cursor.LastCursor, Is.EqualTo(42));
                Assert.That(cursor.CursorExpired, Is.True);
                Assert.That(cursor.EarliestAvailableCursor, Is.EqualTo(40));
                Assert.That(cursor.HasCompletedFullReconcile, Is.True);
                Assert.That(cursor.UpdatedAtUtc, Is.EqualTo(updatedAt));
            });
        }

        [Test]
        public async Task InitializeAsync_MigratesExistingCursorAsIncompleteFullReconcile()
        {
            string databasePath = DatabasePath();
            await CreateMigratedStateDatabaseAsync(
                databasePath,
                "20260622002311_AddRemoteFileIdentityMetadata");
            DbConnectionStringBuilder connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (SyncStateDbContext context = new SyncStateDbContext(options))
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO sync_change_cursors
                        (sync_pair_id, last_cursor, cursor_expired, earliest_available_cursor, updated_at_utc)
                    VALUES
                        ('pair-a', 11944, 0, NULL, '2026-07-26 06:13:01');
                    """);
            }

            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
            await store.InitializeAsync();
            SyncChangeCursor cursor = await store.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(cursor.LastCursor, Is.EqualTo(11944));
                Assert.That(cursor.HasCompletedFullReconcile, Is.False);
            });
        }

        [Test]
        public async Task SaveChangeCursorAsync_UpdatesExistingPairOnly()
        {
            SqliteSyncStateStore store = CreateStore();
            await store.InitializeAsync();
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 1,
            });
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-b",
                LastCursor = 7,
            });

            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 2,
                EarliestAvailableCursor = 1,
            });

            SyncChangeCursor pairA = await store.GetChangeCursorAsync("pair-a");
            SyncChangeCursor pairB = await store.GetChangeCursorAsync("pair-b");

            Assert.Multiple(() =>
            {
                Assert.That(pairA.LastCursor, Is.EqualTo(2));
                Assert.That(pairA.EarliestAvailableCursor, Is.EqualTo(1));
                Assert.That(pairB.LastCursor, Is.EqualTo(7));
                Assert.That(pairB.EarliestAvailableCursor, Is.Null);
            });
        }

    }
}
