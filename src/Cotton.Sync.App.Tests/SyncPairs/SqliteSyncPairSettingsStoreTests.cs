// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.State;
using Microsoft.Data.Sqlite;

namespace Cotton.Sync.App.Tests.SyncPairs
{
    public class SqliteSyncPairSettingsStoreTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-sync-app-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public async Task ListAsync_ReturnsEmptyListForNewDatabase()
        {
            SqliteSyncPairSettingsStore store = CreateStore();
            await store.InitializeAsync();

            IReadOnlyList<SyncPairSettings> syncPairs = await store.ListAsync();

            Assert.That(syncPairs, Is.Empty);
        }

        [Test]
        public async Task InitializeAsync_CreatesParentDirectory()
        {
            string databasePath = Path.Combine(_tempDirectory, "nested", "settings.sqlite");
            var store = new SqliteSyncPairSettingsStore(databasePath);

            await store.InitializeAsync();

            Assert.That(File.Exists(databasePath), Is.True);
        }

        [Test]
        public async Task UpsertAsync_RoundtripsAfterReopen()
        {
            string databasePath = DatabasePath();
            var firstStore = new SqliteSyncPairSettingsStore(databasePath);
            await firstStore.InitializeAsync();
            SyncPairSettings expected = CreatePair("Documents", "/home/user/Documents", "/Documents");

            await firstStore.UpsertAsync(expected);

            var secondStore = new SqliteSyncPairSettingsStore(databasePath);
            await secondStore.InitializeAsync();
            SyncPairSettings? actual = await secondStore.GetAsync(expected.Id);

            Assert.Multiple(() =>
            {
                Assert.That(actual, Is.Not.Null);
                Assert.That(actual!.Id, Is.EqualTo(expected.Id));
                Assert.That(actual.DisplayName, Is.EqualTo("Documents"));
                Assert.That(actual.LocalRootPath, Is.EqualTo("/home/user/Documents"));
                Assert.That(actual.RemoteRootNodeId, Is.EqualTo(expected.RemoteRootNodeId));
                Assert.That(actual.RemoteDisplayPath, Is.EqualTo("/Documents"));
                Assert.That(actual.IsEnabled, Is.True);
                Assert.That(actual.Mode, Is.EqualTo(SyncPairMode.FullMirror));
                Assert.That(actual.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
                Assert.That(actual.UpdatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            });
        }

        [Test]
        public async Task GetAsync_NormalizesLegacyFullMirrorModeZero()
        {
            string databasePath = DatabasePath();
            Guid syncPairId = Guid.NewGuid();
            var contextFactory = new SqliteSyncAppDbContextFactory(databasePath);
            await contextFactory.MigrateAsync(CancellationToken.None);
            await using (SyncAppDbContext context = contextFactory.Create())
            {
                context.SyncPairSettings.Add(new SyncPairSettingsEntity
                {
                    Id = syncPairId,
                    DisplayName = "Documents",
                    LocalRootPath = "/home/user/Documents",
                    RemoteRootNodeId = Guid.NewGuid(),
                    RemoteDisplayPath = "/Documents",
                    IsEnabled = true,
                    Mode = SyncPairMode.Unknown,
                    CreatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
                });
                await context.SaveChangesAsync();
            }

            var store = new SqliteSyncPairSettingsStore(databasePath);
            SyncPairSettings? actual = await store.GetAsync(syncPairId);

            Assert.That(actual?.Mode, Is.EqualTo(SyncPairMode.FullMirror));
        }

        [Test]
        public async Task InitializeAsync_MigratesLegacyReservedModeTwoToFullMirror()
        {
            string databasePath = DatabasePath();
            Guid syncPairId = Guid.NewGuid();
            await CreateLegacyDatabaseBeforeVirtualFilesMigrationAsync(databasePath, syncPairId);
            var store = new SqliteSyncPairSettingsStore(databasePath);

            await store.InitializeAsync();
            SyncPairSettings? actual = await store.GetAsync(syncPairId);

            Assert.Multiple(() =>
            {
                Assert.That(actual, Is.Not.Null);
                Assert.That(actual!.Mode, Is.EqualTo(SyncPairMode.FullMirror));
            });
        }

        [Test]
        public async Task UpsertAsync_PreservesExplicitWindowsVirtualFilesModeAfterMigration()
        {
            SqliteSyncPairSettingsStore store = CreateStore();
            await store.InitializeAsync();
            SyncPairSettings syncPair = CreatePair("Virtual", @"S:\CottonVirtual", "/Virtual");
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            await store.UpsertAsync(syncPair);
            SyncPairSettings? actual = await store.GetAsync(syncPair.Id);

            Assert.That(actual?.Mode, Is.EqualTo(SyncPairMode.WindowsVirtualFiles));
        }

        [Test]
        public async Task UpsertAsync_RejectsUnknownMode()
        {
            SqliteSyncPairSettingsStore store = CreateStore();
            await store.InitializeAsync();
            SyncPairSettings syncPair = CreatePair("Documents", "/home/user/Documents", "/Documents");
            syncPair.Mode = SyncPairMode.Unknown;

            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertAsync(syncPair));
        }

        [Test]
        public async Task UpsertAsync_UpdatesExistingPair()
        {
            SqliteSyncPairSettingsStore store = CreateStore();
            await store.InitializeAsync();
            SyncPairSettings syncPair = CreatePair("Documents", "/home/user/Documents", "/Documents");
            await store.UpsertAsync(syncPair);

            syncPair.DisplayName = "Work";
            syncPair.LocalRootPath = "/home/user/Work";
            syncPair.RemoteDisplayPath = "/Work";
            syncPair.IsEnabled = false;
            await store.UpsertAsync(syncPair);

            IReadOnlyList<SyncPairSettings> syncPairs = await store.ListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(syncPairs, Has.Count.EqualTo(1));
                Assert.That(syncPairs[0].DisplayName, Is.EqualTo("Work"));
                Assert.That(syncPairs[0].LocalRootPath, Is.EqualTo("/home/user/Work"));
                Assert.That(syncPairs[0].RemoteDisplayPath, Is.EqualTo("/Work"));
                Assert.That(syncPairs[0].IsEnabled, Is.False);
            });
        }

        [Test]
        public async Task DeleteAsync_RemovesOnlyRequestedPair()
        {
            SqliteSyncPairSettingsStore store = CreateStore();
            await store.InitializeAsync();
            SyncPairSettings first = CreatePair("Documents", "/home/user/Documents", "/Documents");
            SyncPairSettings second = CreatePair("Pictures", "/home/user/Pictures", "/Pictures");
            await store.UpsertAsync(first);
            await store.UpsertAsync(second);

            await store.DeleteAsync(first.Id);

            SyncPairSettings? deleted = await store.GetAsync(first.Id);
            IReadOnlyList<SyncPairSettings> syncPairs = await store.ListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(deleted, Is.Null);
                Assert.That(syncPairs.Select(pair => pair.Id), Is.EqualTo(new[] { second.Id }));
            });
        }

        private SqliteSyncPairSettingsStore CreateStore()
        {
            return new SqliteSyncPairSettingsStore(DatabasePath());
        }

        private string DatabasePath()
        {
            return Path.Combine(_tempDirectory, "settings.sqlite");
        }

        private static async Task CreateLegacyDatabaseBeforeVirtualFilesMigrationAsync(
            string databasePath,
            Guid syncPairId)
        {
            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );

                CREATE TABLE "app_preferences" (
                    "id" INTEGER NOT NULL CONSTRAINT "PK_app_preferences" PRIMARY KEY AUTOINCREMENT,
                    "remembered_server_url" TEXT NOT NULL,
                    "remembered_username" TEXT NOT NULL,
                    "start_with_operating_system" INTEGER NOT NULL,
                    "start_minimized_to_tray" INTEGER NOT NULL,
                    "enable_notifications" INTEGER NOT NULL,
                    "theme_mode" INTEGER NOT NULL,
                    "is_sync_paused" INTEGER NOT NULL,
                    "created_at_utc" TEXT NOT NULL,
                    "updated_at_utc" TEXT NOT NULL
                );

                CREATE TABLE "sync_pair_settings" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_sync_pair_settings" PRIMARY KEY,
                    "display_name" TEXT NOT NULL,
                    "local_root_path" TEXT NOT NULL,
                    "remote_root_node_id" TEXT NOT NULL,
                    "remote_display_path" TEXT NOT NULL,
                    "is_enabled" INTEGER NOT NULL,
                    "mode" INTEGER NOT NULL,
                    "created_at_utc" TEXT NOT NULL,
                    "updated_at_utc" TEXT NOT NULL
                );

                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
                    ('20260603192255_InitialSyncAppState', '10.0.8'),
                    ('20260603211332_AddAppThemePreference', '10.0.8'),
                    ('20260607084529_AddSyncPausedPreference', '10.0.8');
                """);

            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO "sync_pair_settings" (
                    "id",
                    "display_name",
                    "local_root_path",
                    "remote_root_node_id",
                    "remote_display_path",
                    "is_enabled",
                    "mode",
                    "created_at_utc",
                    "updated_at_utc")
                VALUES (
                    $id,
                    'Legacy reserved mode',
                    '/home/user/Legacy',
                    $remoteRootNodeId,
                    '/Legacy',
                    1,
                    2,
                    '2026-06-03T10:00:00.0000000Z',
                    '2026-06-03T10:00:00.0000000Z');
                """;
            insert.Parameters.AddWithValue("$id", syncPairId.ToString().ToUpperInvariant());
            insert.Parameters.AddWithValue("$remoteRootNodeId", Guid.NewGuid().ToString().ToUpperInvariant());
            await insert.ExecuteNonQueryAsync();
        }

        private static async Task ExecuteAsync(SqliteConnection connection, string commandText)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
        }

        private static SyncPairSettings CreatePair(string displayName, string localRootPath, string remoteDisplayPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                LocalRootPath = localRootPath,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = remoteDisplayPath,
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
                CreatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc),
            };
        }
    }
}
