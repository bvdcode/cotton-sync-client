// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesHydratedEditUploadsNormally()
        {
            const string relativePath = "hydrated-edited.txt";
            string oldHash = HashText("old-content");
            WriteFile(relativePath, "local-new-content");
            NodeFileManifestDto remote = RemoteFile(relativePath, oldHash, sizeBytes: Encoding.UTF8.GetByteCount("old-content"));
            LocalFileScanner scanner = new LocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = oldHash,
                LocalLastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
                LocalSizeBytes = Encoding.UTF8.GetByteCount("old-content"),
                RemoteNodeId = remote.NodeId,
                RemoteFileId = remote.Id,
                RemoteSizeBytes = remote.SizeBytes,
                RemoteContentHash = remote.ContentHash,
                RemoteETag = remote.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile?.Id, Is.EqualTo(remote.Id));
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(HashText("local-new-content")));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(HashText("local-new-content")));
            });
        }
    }
}
