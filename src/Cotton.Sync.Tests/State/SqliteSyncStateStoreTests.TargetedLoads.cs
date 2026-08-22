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
        public async Task LoadEntriesByPathKeysAsync_LoadsOnlyRequestedKeysInPathOrder()
        {
            SqliteSyncStateStore store = CreateStore();
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

            List<SyncStateEntry> entries = new List<SyncStateEntry>();
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
            SqliteSyncStateStore store = CreateStore();
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

            List<SyncStateEntry> entries = new List<SyncStateEntry>();
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
            SqliteSyncStateStore store = CreateStore();
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

            List<SyncVirtualFilesResumeEntry> entries = new List<SyncVirtualFilesResumeEntry>();
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
            SqliteSyncStateStore store = CreateStore();
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

    }
}
