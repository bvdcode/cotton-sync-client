// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Tests.State
{
    public partial class SqliteSyncStateStoreTests
    {
        [TestCase("Pictures")]
        [TestCase("Фото_100%")]
        public async Task DeleteByPathPrefixAsync_DeletesOnlyExactSubtreeAndPreservesCursor(string rootPath)
        {
            SqliteSyncStateStore store = CreateStore();
            await store.InitializeAsync();
            string[] paths = [rootPath, rootPath + "/Nested", rootPath + "/Nested/photo.jpg", rootPath + "-keep", "Other/photo.jpg"];
            foreach (string pairId in new[] { "pair-a", "pair-b" })
            {
                await store.UpsertManyAsync(paths.Select(path => new SyncStateEntry
                {
                    SyncPairId = pairId,
                    RelativePath = path,
                    Kind = SyncEntryKind.File,
                }).ToArray());
            }
            await store.SaveChangeCursorAsync(new SyncChangeCursor { SyncPairId = "pair-a", LastCursor = 12 });

            await store.DeleteByPathPrefixAsync("pair-a", rootPath.ToLowerInvariant());

            IReadOnlyList<SyncStateEntry> remaining = await store.LoadPairAsync("pair-a");
            IReadOnlyList<SyncStateEntry> otherPair = await store.LoadPairAsync("pair-b");
            SyncChangeCursor cursor = await store.GetChangeCursorAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remaining.Select(entry => entry.RelativePath), Is.EquivalentTo(new[] { rootPath + "-keep", "Other/photo.jpg" }));
                Assert.That(otherPair, Has.Count.EqualTo(paths.Length));
                Assert.That(cursor.LastCursor, Is.EqualTo(12));
            });
        }

        [TestCase("")]
        [TestCase("../Pictures")]
        public void DeleteByPathPrefixAsync_RejectsInvalidPrefix(string prefix)
        {
            Assert.That(() => CreateStore().DeleteByPathPrefixAsync("pair-a", prefix), Throws.Exception);
        }
    }
}
