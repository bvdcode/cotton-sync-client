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
        public async Task UpsertAsync_RoundtripsAndPersistsAfterReopen()
        {
            string databasePath = DatabasePath();
            SqliteSyncStateStore first = new SqliteSyncStateStore(databasePath);
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

            SqliteSyncStateStore second = new SqliteSyncStateStore(databasePath);
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
            SqliteSyncStateStore first = new SqliteSyncStateStore(databasePath);
            await first.InitializeAsync();
            await first.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Docs/Empty",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = nodeId,
                SyncedAtUtc = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc),
            });

            SqliteSyncStateStore second = new SqliteSyncStateStore(databasePath);
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
            SqliteSyncStateStore store = CreateStore();
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
            SqliteSyncStateStore store = CreateStore();
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

            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
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
            SqliteSyncStateStore store = CreateStore();
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
            SqliteSyncStateStore store = CreateStore();
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
            SqliteSyncStateStore store = new SqliteSyncStateStore(databasePath);
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
            SyncStateStoreDiagnostics beforeUsage = await store.GetDiagnosticsAsync();

            await store.DeletePairAsync("pair-a");

            IReadOnlyList<SyncStateEntry> pairA = await store.LoadPairAsync("pair-a");
            IReadOnlyList<SyncStateEntry> pairB = await store.LoadPairAsync("pair-b");
            SyncChangeCursor pairACursor = await store.GetChangeCursorAsync("pair-a");
            long afterLength = new FileInfo(databasePath).Length;
            SyncStateStoreDiagnostics afterUsage = await store.GetDiagnosticsAsync();

            Assert.Multiple(() =>
            {
                Assert.That(beforeUsage.FileSizeBytes, Is.GreaterThan(4L * 1024 * 1024));
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

    }
}
