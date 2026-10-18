// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
    {
        [Test]
        public async Task PendingConversions_SurviveReopenAndExcludeIncompleteTransfersAndFinishedPlaceholders()
        {
            string databasePath = Path.Combine(_tempDirectory, "pending-conversions.db");
            SqliteSyncStateStore store = new(databasePath);
            SyncStateEntry CreateEntry(string path) => new()
            {
                SyncPairId = "pair-a",
                RelativePath = path,
                Kind = SyncEntryKind.File,
                RemoteNodeId = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
                RemoteFileManifestId = Guid.NewGuid(),
                LocalContentHash = "abcdef",
                RemoteContentHash = "ABCDEF",
                LocalSizeBytes = 123,
                LocalLastWriteUtc = DateTime.UtcNow,
            };
            SyncStateEntry pending = CreateEntry("pending.mp4");
            SyncStateEntry completed = CreateEntry("completed.mp4");
            completed.PlaceholderIdentity = [1, 2, 3];
            SyncStateEntry changed = CreateEntry("different-content.mp4");
            changed.RemoteContentHash = "fedcba";
            SyncStateEntry remoteOnly = CreateEntry("remote-only.mp4");
            remoteOnly.LocalContentHash = null;
            SyncStateEntry missingIdentity = CreateEntry("missing-remote-id.mp4");
            missingIdentity.RemoteFileId = null;
            SyncStateEntry directory = CreateEntry("directory");
            directory.Kind = SyncEntryKind.Directory;
            SyncStateEntry otherPair = CreateEntry("other-pair.mp4");
            otherPair.SyncPairId = "pair-b";
            foreach (SyncStateEntry entry in new[] { pending, completed, changed, remoteOnly, missingIdentity, directory, otherPair })
            {
                await store.UpsertAsync(entry);
            }

            store = new SqliteSyncStateStore(databasePath);
            List<string> pendingPaths = [];
            await foreach (string path in store.LoadPendingPlaceholderConversionPathsAsync("pair-a"))
            {
                pendingPaths.Add(path);
            }
            IReadOnlyList<SyncStateEntry> savedEntries = await store.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(pendingPaths, Is.EqualTo(new[] { "pending.mp4" }));
                Assert.That(savedEntries.Where(static entry => entry.NeedsPlaceholderConversion)
                    .Select(static entry => entry.RelativePath), Is.EqualTo(pendingPaths));
            });

            pending.PlaceholderIdentity = [4, 5, 6];
            await store.UpsertAsync(pending);
            store = new SqliteSyncStateStore(databasePath);
            await foreach (string path in store.LoadPendingPlaceholderConversionPathsAsync("pair-a"))
            {
                Assert.Fail("Completed conversion remained pending: " + path);
            }
        }
    }
}
