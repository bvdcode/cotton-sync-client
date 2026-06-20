// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Data.Common;
using Cotton.Sync.State;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.Tests.State
{
    public class SqliteSyncStateStoreTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-sync-tests", Guid.NewGuid().ToString("N"));
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
        public async Task LoadPairAsync_ReturnsEmptyListForNewDatabase()
        {
            var store = CreateStore();
            await store.InitializeAsync();

            IReadOnlyList<SyncStateEntry> entries = await store.LoadPairAsync("pair-a");

            Assert.That(entries, Is.Empty);
        }

        [Test]
        public async Task LoadPairEntriesAsync_StreamsEntriesInPathOrder()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            DateTime firstSyncedAtUtc = new(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);
            DateTime lastSyncedAtUtc = new(2026, 6, 7, 10, 5, 0, DateTimeKind.Utc);
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "z-last.txt",
                Kind = SyncEntryKind.File,
                SyncedAtUtc = firstSyncedAtUtc,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "a-first.txt",
                Kind = SyncEntryKind.File,
                SyncedAtUtc = lastSyncedAtUtc,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "ignored.txt",
                Kind = SyncEntryKind.File,
                SyncedAtUtc = lastSyncedAtUtc.AddMinutes(1),
            });

            var entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadPairEntriesAsync("pair-a"))
            {
                entries.Add(entry);
            }
            DateTime? pairLastSyncedAtUtc = await store.GetPairLastSyncedAtUtcAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(entries.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "a-first.txt", "z-last.txt" }));
                Assert.That(pairLastSyncedAtUtc, Is.EqualTo(lastSyncedAtUtc));
            });
        }

        [Test]
        public async Task LoadPairDirectoryEntriesAsync_StreamsOnlyDirectoriesInPathOrder()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            Guid parentNodeId = Guid.NewGuid();
            Guid directoryNodeId = Guid.NewGuid();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/Archive",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = directoryNodeId,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = parentNodeId,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/report.txt",
                Kind = SyncEntryKind.File,
                RemoteNodeId = parentNodeId,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "Docs",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });

            var entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadPairDirectoryEntriesAsync("pair-a"))
            {
                entries.Add(entry);
            }

            Assert.Multiple(() =>
            {
                Assert.That(entries.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Docs", "Docs/Archive" }));
                Assert.That(entries.Select(entry => entry.Kind), Is.All.EqualTo(SyncEntryKind.Directory));
                Assert.That(entries.Select(entry => entry.RemoteNodeId), Is.EqualTo(new[] { parentNodeId, directoryNodeId }));
            });
        }

        [Test]
        public async Task LoadEntriesByPathKeysAsync_LoadsOnlyRequestedKeysInPathOrder()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "z-last.txt",
                Kind = SyncEntryKind.File,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "a-first.txt",
                Kind = SyncEntryKind.File,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "ignored.txt",
                Kind = SyncEntryKind.File,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "z-last.txt",
                Kind = SyncEntryKind.File,
            });

            var entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadEntriesByPathKeysAsync(
                               "pair-a",
                               ["z-last.txt", "missing.txt", "a-first.txt"]))
            {
                entries.Add(entry);
            }

            Assert.That(entries.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "a-first.txt", "z-last.txt" }));
        }

        [Test]
        public async Task LoadEntriesByRemoteIdsAsync_LoadsDirectoryAndFileTargetsWithoutParentFileFanout()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            Guid parentNodeId = Guid.NewGuid();
            Guid directoryNodeId = Guid.NewGuid();
            Guid requestedFileId = Guid.NewGuid();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = parentNodeId,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/Archive",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = directoryNodeId,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/report.txt",
                Kind = SyncEntryKind.File,
                RemoteNodeId = parentNodeId,
                RemoteFileId = requestedFileId,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/sibling.txt",
                Kind = SyncEntryKind.File,
                RemoteNodeId = parentNodeId,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "Docs/report.txt",
                Kind = SyncEntryKind.File,
                RemoteNodeId = parentNodeId,
                RemoteFileId = requestedFileId,
            });

            var entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadEntriesByRemoteIdsAsync(
                               "pair-a",
                               [parentNodeId, directoryNodeId],
                               [requestedFileId]))
            {
                entries.Add(entry);
            }

            Assert.That(
                entries.Select(entry => entry.RelativePath),
                Is.EqualTo(new[] { "Docs", "Docs/Archive", "Docs/report.txt" }));
        }

        [Test]
        public async Task LoadVirtualFilesResumeEntriesByPathKeysAsync_LoadsCompactResumeRows()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            Guid remoteNodeId = Guid.NewGuid();
            Guid remoteFileId = Guid.NewGuid();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = remoteNodeId,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/remote-only.txt",
                Kind = SyncEntryKind.File,
                RemoteFileId = remoteFileId,
                RemoteContentHash = "remote-hash",
                RemoteETag = "etag-1",
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "Docs/ignored.txt",
                Kind = SyncEntryKind.File,
                PlaceholderIdentity = [0x01],
            });

            var entries = new List<SyncVirtualFilesResumeEntry>();
            await foreach (SyncVirtualFilesResumeEntry entry in store.LoadVirtualFilesResumeEntriesByPathKeysAsync(
                               "pair-a",
                               ["Docs/remote-only.txt", "Docs", "missing.txt"]))
            {
                entries.Add(entry);
            }

            SyncVirtualFilesResumeEntry directory = entries.Single(entry => entry.Kind == SyncEntryKind.Directory);
            SyncVirtualFilesResumeEntry file = entries.Single(entry => entry.Kind == SyncEntryKind.File);
            Assert.Multiple(() =>
            {
                Assert.That(entries.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Docs", "Docs/remote-only.txt" }));
                Assert.That(directory.RemoteNodeId, Is.EqualTo(remoteNodeId));
                Assert.That(file.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(file.RemoteContentHash, Is.EqualTo("remote-hash"));
                Assert.That(file.RemoteETag, Is.EqualTo("etag-1"));
                Assert.That(file.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(file.HasPlaceholderIdentity, Is.True);
            });
        }

        [Test]
        public async Task UpsertManyAsync_InsertsAndUpdatesEntriesInOneBatch()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "existing.txt",
                Kind = SyncEntryKind.File,
                RemoteContentHash = "old-hash",
                RemoteSizeBytes = 1,
            });

            await store.UpsertManyAsync(
                [
                    new SyncStateEntry
                    {
                        SyncPairId = "pair-a",
                        RelativePath = "existing.txt",
                        Kind = SyncEntryKind.File,
                        RemoteContentHash = "new-hash",
                        RemoteSizeBytes = 2,
                    },
                    new SyncStateEntry
                    {
                        SyncPairId = "pair-a",
                        RelativePath = "new.txt",
                        Kind = SyncEntryKind.File,
                        RemoteContentHash = "new-file-hash",
                        RemoteSizeBytes = 3,
                    },
                ]);

            IReadOnlyList<SyncStateEntry> entries = await store.LoadPairAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(entries, Has.Count.EqualTo(2));
                Assert.That(entries.Single(entry => entry.RelativePath == "existing.txt").RemoteContentHash, Is.EqualTo("new-hash"));
                Assert.That(entries.Single(entry => entry.RelativePath == "existing.txt").RemoteSizeBytes, Is.EqualTo(2));
                Assert.That(entries.Single(entry => entry.RelativePath == "new.txt").RemoteContentHash, Is.EqualTo("new-file-hash"));
            });
        }

        [Test]
        public async Task GetChangeCursorAsync_ReturnsDefaultCursorForNewPair()
        {
            var store = CreateStore();
            await store.InitializeAsync();

            SyncChangeCursor cursor = await store.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(cursor.SyncPairId, Is.EqualTo("pair-a"));
                Assert.That(cursor.LastCursor, Is.Zero);
                Assert.That(cursor.CursorExpired, Is.False);
                Assert.That(cursor.EarliestAvailableCursor, Is.Null);
                Assert.That(cursor.UpdatedAtUtc, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
            });
        }

        [Test]
        public async Task GetChangeCursorAsync_InitializesNewDatabaseWithoutExplicitInitialize()
        {
            var store = CreateStore();

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
            var first = new SqliteSyncStateStore(databasePath);

            await first.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 9,
            });

            var second = new SqliteSyncStateStore(databasePath);
            SyncChangeCursor cursor = await second.GetChangeCursorAsync("pair-a");

            Assert.That(cursor.LastCursor, Is.EqualTo(9));
        }

        [Test]
        public async Task InitializeAsync_MigratesInitialStateDatabaseToChangeCursors()
        {
            string databasePath = DatabasePath();
            await CreateInitialStateDatabaseAsync(databasePath);
            var store = new SqliteSyncStateStore(databasePath);

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
        public async Task InitializeAsync_MigratesLocalSizeStateDatabaseToVirtualFileMetadata()
        {
            string databasePath = DatabasePath();
            await CreateLocalSizeStateDatabaseAsync(databasePath);
            var store = new SqliteSyncStateStore(databasePath);
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
            var store = CreateStore();

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
            var first = new SqliteSyncStateStore(databasePath);
            await first.InitializeAsync();
            var updatedAt = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
            await first.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 42,
                CursorExpired = true,
                EarliestAvailableCursor = 40,
                UpdatedAtUtc = updatedAt,
            });

            var second = new SqliteSyncStateStore(databasePath);
            await second.InitializeAsync();
            SyncChangeCursor cursor = await second.GetChangeCursorAsync("pair-a");

            Assert.Multiple(() =>
            {
                Assert.That(cursor.SyncPairId, Is.EqualTo("pair-a"));
                Assert.That(cursor.LastCursor, Is.EqualTo(42));
                Assert.That(cursor.CursorExpired, Is.True);
                Assert.That(cursor.EarliestAvailableCursor, Is.EqualTo(40));
                Assert.That(cursor.UpdatedAtUtc, Is.EqualTo(updatedAt));
            });
        }

        [Test]
        public async Task SaveChangeCursorAsync_UpdatesExistingPairOnly()
        {
            var store = CreateStore();
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

        [Test]
        public async Task UpsertAsync_RoundtripsAndPersistsAfterReopen()
        {
            string databasePath = DatabasePath();
            var first = new SqliteSyncStateStore(databasePath);
            await first.InitializeAsync();
            Guid fileId = Guid.NewGuid();
            Guid nodeId = Guid.NewGuid();
            byte[] placeholderIdentity = [0x43, 0x46, 0x41, 0x50, 0x49];
            await first.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/Report.txt",
                Kind = SyncEntryKind.File,
                LocalContentHash = "local-hash",
                LocalLastWriteUtc = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc),
                LocalSizeBytes = 4096,
                RemoteNodeId = nodeId,
                RemoteFileId = fileId,
                RemoteSizeBytes = 8192,
                RemoteContentHash = "remote-hash",
                RemoteETag = "sha256-remote-hash",
                PlaceholderIdentity = placeholderIdentity,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                SyncedAtUtc = new DateTime(2026, 6, 2, 12, 1, 0, DateTimeKind.Utc),
            });

            var second = new SqliteSyncStateStore(databasePath);
            await second.InitializeAsync();
            SyncStateEntry? entry = await second.GetAsync("pair-a", "docs/report.TXT");

            Assert.Multiple(() =>
            {
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RelativePath, Is.EqualTo("Docs/Report.txt"));
                Assert.That(entry.Kind, Is.EqualTo(SyncEntryKind.File));
                Assert.That(entry.LocalContentHash, Is.EqualTo("local-hash"));
                Assert.That(entry.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)));
                Assert.That(entry.LocalSizeBytes, Is.EqualTo(4096));
                Assert.That(entry.RemoteNodeId, Is.EqualTo(nodeId));
                Assert.That(entry.RemoteFileId, Is.EqualTo(fileId));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(8192));
                Assert.That(entry.RemoteContentHash, Is.EqualTo("remote-hash"));
                Assert.That(entry.RemoteETag, Is.EqualTo("sha256-remote-hash"));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
            });
        }

        [Test]
        public async Task UpsertAsync_RoundtripsDirectoryEntry()
        {
            string databasePath = DatabasePath();
            Guid nodeId = Guid.NewGuid();
            var first = new SqliteSyncStateStore(databasePath);
            await first.InitializeAsync();
            await first.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/Empty",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = nodeId,
                SyncedAtUtc = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc),
            });

            var second = new SqliteSyncStateStore(databasePath);
            await second.InitializeAsync();
            SyncStateEntry? entry = await second.GetAsync("pair-a", "docs/empty");

            Assert.Multiple(() =>
            {
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.Kind, Is.EqualTo(SyncEntryKind.Directory));
                Assert.That(entry.RelativePath, Is.EqualTo("Docs/Empty"));
                Assert.That(entry.RemoteNodeId, Is.EqualTo(nodeId));
                Assert.That(entry.RemoteFileId, Is.Null);
                Assert.That(entry.LocalContentHash, Is.Null);
                Assert.That(entry.RemoteContentHash, Is.Null);
            });
        }

        [Test]
        public async Task UpsertAsync_UsesCaseInsensitivePathKeyWithinPair()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Folder/File.txt",
                Kind = SyncEntryKind.File,
                LocalContentHash = "first",
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = @"folder\file.TXT",
                Kind = SyncEntryKind.File,
                LocalContentHash = "second",
            });

            IReadOnlyList<SyncStateEntry> entries = await store.LoadPairAsync("pair-a");
            SyncStateEntry? entry = await store.GetAsync("pair-a", "FOLDER/file.txt");

            Assert.Multiple(() =>
            {
                Assert.That(entries, Has.Count.EqualTo(1));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RelativePath, Is.EqualTo("folder/file.TXT"));
                Assert.That(entry.LocalContentHash, Is.EqualTo("second"));
            });
        }

        [Test]
        public async Task ReplacePairAsync_ReplacesOnlyRequestedPair()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "old.txt",
                Kind = SyncEntryKind.File,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "keep.txt",
                Kind = SyncEntryKind.File,
            });

            await store.ReplacePairAsync("pair-a", new[]
            {
                new SyncStateEntry
                {
                    RelativePath = "new.txt",
                    Kind = SyncEntryKind.File,
                    LocalContentHash = "new",
                    RemoteSizeBytes = 2048,
                    PlaceholderIdentity = [0x10, 0x20],
                    PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                },
            });

            IReadOnlyList<SyncStateEntry> pairA = await store.LoadPairAsync("pair-a");
            IReadOnlyList<SyncStateEntry> pairB = await store.LoadPairAsync("pair-b");

            Assert.Multiple(() =>
            {
                Assert.That(pairA.Select(x => x.RelativePath), Is.EqualTo(new[] { "new.txt" }));
                Assert.That(pairA.Single().RemoteSizeBytes, Is.EqualTo(2048));
                Assert.That(pairA.Single().PlaceholderIdentity, Is.EqualTo(new byte[] { 0x10, 0x20 }));
                Assert.That(pairA.Single().PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(pairB.Select(x => x.RelativePath), Is.EqualTo(new[] { "keep.txt" }));
            });
        }

        [Test]
        public async Task InitializeAsync_SerializesConcurrentStoresSharingDatabase()
        {
            string databasePath = DatabasePath();
            Task[] migrations = Enumerable.Range(0, 12)
                .Select(_ => new SqliteSyncStateStore(databasePath).InitializeAsync())
                .ToArray();

            await Task.WhenAll(migrations);

            var store = new SqliteSyncStateStore(databasePath);
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
        public async Task DeleteAsync_RemovesOneEntryOnly()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "delete.txt",
                Kind = SyncEntryKind.File,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "keep.txt",
                Kind = SyncEntryKind.File,
            });

            await store.DeleteAsync("pair-a", "DELETE.txt");

            IReadOnlyList<SyncStateEntry> entries = await store.LoadPairAsync("pair-a");
            Assert.That(entries.Select(x => x.RelativePath), Is.EqualTo(new[] { "keep.txt" }));
        }

        [Test]
        public async Task DeletePairAsync_RemovesEntriesAndCursorForRequestedPairOnly()
        {
            var store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "delete.txt",
                Kind = SyncEntryKind.File,
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "keep.txt",
                Kind = SyncEntryKind.File,
            });
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 10,
            });
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-b",
                LastCursor = 20,
            });

            await store.DeletePairAsync("pair-a");

            IReadOnlyList<SyncStateEntry> pairA = await store.LoadPairAsync("pair-a");
            IReadOnlyList<SyncStateEntry> pairB = await store.LoadPairAsync("pair-b");
            SyncChangeCursor pairACursor = await store.GetChangeCursorAsync("pair-a");
            SyncChangeCursor pairBCursor = await store.GetChangeCursorAsync("pair-b");

            Assert.Multiple(() =>
            {
                Assert.That(pairA, Is.Empty);
                Assert.That(pairB.Select(x => x.RelativePath), Is.EqualTo(new[] { "keep.txt" }));
                Assert.That(pairACursor.LastCursor, Is.Zero);
                Assert.That(pairBCursor.LastCursor, Is.EqualTo(20));
            });
        }

        [Test]
        public async Task DeletePairAsync_CompactsLargeFreelistAfterRemovingLargePair()
        {
            string databasePath = DatabasePath();
            var store = new SqliteSyncStateStore(databasePath);
            await store.InitializeAsync();
            byte[] placeholderIdentity = Enumerable.Range(0, 16 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();
            SyncStateEntry[] largePairEntries = Enumerable.Range(0, 512)
                .Select(index => new SyncStateEntry
                {
                    SyncPairId = "pair-a",
                    RelativePath = "Large/file-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
                    Kind = SyncEntryKind.File,
                    RemoteFileId = Guid.NewGuid(),
                    RemoteContentHash = "hash-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    RemoteETag = "etag-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    PlaceholderIdentity = placeholderIdentity,
                    PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                })
                .ToArray();

            await store.UpsertManyAsync(largePairEntries);
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "keep.txt",
                Kind = SyncEntryKind.File,
                RemoteContentHash = "keep",
            });
            await store.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = "pair-a",
                LastCursor = 123,
            });

            long beforeLength = new FileInfo(databasePath).Length;
            SqlitePageUsage beforeUsage = await ReadPageUsageAsync(databasePath);

            await store.DeletePairAsync("pair-a");

            IReadOnlyList<SyncStateEntry> pairA = await store.LoadPairAsync("pair-a");
            IReadOnlyList<SyncStateEntry> pairB = await store.LoadPairAsync("pair-b");
            SyncChangeCursor pairACursor = await store.GetChangeCursorAsync("pair-a");
            long afterLength = new FileInfo(databasePath).Length;
            SqlitePageUsage afterUsage = await ReadPageUsageAsync(databasePath);

            Assert.Multiple(() =>
            {
                Assert.That(beforeUsage.FileBytes, Is.GreaterThan(4L * 1024 * 1024));
                Assert.That(pairA, Is.Empty);
                Assert.That(pairB.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "keep.txt" }));
                Assert.That(pairACursor.LastCursor, Is.Zero);
                Assert.That(afterUsage.FreelistBytes, Is.LessThan(1024 * 1024));
                Assert.That(afterLength, Is.LessThan(beforeLength / 2));
            });
        }

        private SqliteSyncStateStore CreateStore()
        {
            return new SqliteSyncStateStore(DatabasePath());
        }

        private string DatabasePath()
        {
            return Path.Combine(_tempDirectory, "sync-state.sqlite");
        }

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
            var connectionString = new System.Data.Common.DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using var context = new SyncStateDbContext(options);
            await context.Database.MigrateAsync(migration);
        }

        private static async Task<SqlitePageUsage> ReadPageUsageAsync(string databasePath)
        {
            var connectionString = new DbConnectionStringBuilder
            {
                ["Data Source"] = databasePath,
                ["Pooling"] = false,
            }.ToString();
            DbContextOptions<SyncStateDbContext> options = new DbContextOptionsBuilder<SyncStateDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using var context = new SyncStateDbContext(options);
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

        private sealed record SqlitePageUsage(long PageCount, long FreelistCount, long PageSize)
        {
            public long FileBytes => PageCount * PageSize;

            public long FreelistBytes => FreelistCount * PageSize;
        }
    }
}
