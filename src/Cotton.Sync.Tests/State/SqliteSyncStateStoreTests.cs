// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Data.Common;
using Cotton.Sync.State;
using Microsoft.EntityFrameworkCore;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
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
            SqliteSyncStateStore store = CreateStore();
            await store.InitializeAsync();

            IReadOnlyList<SyncStateEntry> entries = await store.LoadPairAsync("pair-a");

            Assert.That(entries, Is.Empty);
        }

        [Test]
        public async Task LoadPairEntriesAsync_StreamsEntriesInPathOrder()
        {
            SqliteSyncStateStore store = CreateStore();
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

            List<SyncStateEntry> entries = new List<SyncStateEntry>();
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
            SqliteSyncStateStore store = CreateStore();
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

            List<SyncStateEntry> entries = new List<SyncStateEntry>();
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
        public async Task LoadDirectoryEntriesByPathPrefixAsync_StreamsOnlyMatchingDirectorySubtree()
        {
            SqliteSyncStateStore store = CreateStore();
            await store.InitializeAsync();
            Guid parentNodeId = Guid.NewGuid();
            Guid childNodeId = Guid.NewGuid();
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
                RemoteNodeId = childNodeId,
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
                SyncPairId = "pair-a",
                RelativePath = "Downloads",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "Docs/Archive",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });

            List<SyncStateEntry> entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadDirectoryEntriesByPathPrefixAsync("pair-a", "docs"))
            {
                entries.Add(entry);
            }

            Assert.Multiple(() =>
            {
                Assert.That(entries.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Docs", "Docs/Archive" }));
                Assert.That(entries.Select(entry => entry.Kind), Is.All.EqualTo(SyncEntryKind.Directory));
                Assert.That(entries.Select(entry => entry.RemoteNodeId), Is.EqualTo(new[] { parentNodeId, childNodeId }));
            });
        }

        [Test]
        public async Task LoadEntriesByPathPrefixAsync_StreamsOnlyMatchingFileAndDirectorySubtree()
        {
            SqliteSyncStateStore store = CreateStore();
            await store.InitializeAsync();
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music/Album",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music/Album/track.mp3",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music-old/other.mp3",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music [Live]",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music [Live]/set.mp3",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music %_Live",
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music %_Live/set.mp3",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = "Music aaLive/other.mp3",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
            });
            await store.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "Music/Album/other.mp3",
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.NewGuid(),
            });

            List<SyncStateEntry> entries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadEntriesByPathPrefixAsync("pair-a", "music"))
            {
                entries.Add(entry);
            }

            List<SyncStateEntry> bracketEntries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadEntriesByPathPrefixAsync("pair-a", "music [live]"))
            {
                bracketEntries.Add(entry);
            }

            List<SyncStateEntry> likeWildcardEntries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in store.LoadEntriesByPathPrefixAsync("pair-a", "music %_live"))
            {
                likeWildcardEntries.Add(entry);
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    entries.Select(entry => entry.RelativePath),
                    Is.EqualTo(new[] { "Music", "Music/Album", "Music/Album/track.mp3" }));
                Assert.That(
                    entries.Select(entry => entry.Kind),
                    Is.EqualTo(new[] { SyncEntryKind.Directory, SyncEntryKind.Directory, SyncEntryKind.File }));
                Assert.That(
                    bracketEntries.Select(entry => entry.RelativePath),
                    Is.EqualTo(new[] { "Music [Live]", "Music [Live]/set.mp3" }));
                Assert.That(
                    likeWildcardEntries.Select(entry => entry.RelativePath),
                    Is.EqualTo(new[] { "Music %_Live", "Music %_Live/set.mp3" }));
            });
        }

    }
}
