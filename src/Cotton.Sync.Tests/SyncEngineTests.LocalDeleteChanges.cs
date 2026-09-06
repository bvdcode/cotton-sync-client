// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [Test]
        public async Task RunOnceAsync_StreamingRemoteDeletePreservesFileMaterializedAfterPlanning()
        {
            const string relativePath = "materialized-during-stream.txt";
            WriteFile(relativePath, string.Empty);
            LocalFileSnapshot expected = CloudFilesPlaceholderLocal(relativePath, 8);
            FakeLocalFileScanner scanner = new(expected);
            SqliteSyncStateStore stateStore = new(_databasePath);
            NodeFileManifestDto baseline = RemoteFile(relativePath, HashText("original"), sizeBytes: 8);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baseline);
            BlockingStreamingRemoteTreeCrawler crawler = new(_remoteRootNodeId, [])
            {
                BeforeStreamingCompletes = () =>
                {
                    WriteFile(relativePath, "modified");
                    scanner.Files.Clear();
                    scanner.Files.Add(LocalFile(relativePath, "modified"));
                },
            };
            FakeRemoteFileSynchronizer remote = new();
            SyncEngine engine = new(scanner, crawler, remote, stateStore,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? retained = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(crawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(scanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("modified"));
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { relativePath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Does.Not.Contain(SyncActivityKind.DeletedLocal));
                Assert.That(retained, Is.Not.Null);
                Assert.That(retained!.RemoteFileId, Is.EqualTo(baseline.Id));
                Assert.That(remote.DownloadCalls, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_DefersRemoteDeleteWhenLocalContentChangesAfterScan(bool preserveMetadata)
        {
            const string relativePath = "edited-during-crawl.txt";
            WriteFile(relativePath, "original");
            string fullPath = Path.Combine(_root, relativePath);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(fullPath);
            SqliteSyncStateStore stateStore = new(_databasePath);
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("original"), sizeBytes: 8);
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote, localSizeBytes: 8);
            bool changed = false;
            FakeRemoteTreeCrawler remoteCrawler = new(EmptyRemoteTree())
            {
                BeforeCrawlReturns = () =>
                {
                    if (changed)
                    {
                        return;
                    }

                    changed = true;
                    File.WriteAllText(fullPath, "modified");
                    if (preserveMetadata)
                    {
                        File.SetLastWriteTimeUtc(fullPath, originalWriteTime);
                    }
                },
            };
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = new(new LocalFileScanner(), remoteCrawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? retained = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(fullPath), Is.True);
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("modified"));
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { relativePath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Does.Not.Contain(SyncActivityKind.DeletedLocal));
                Assert.That(retained, Is.Not.Null);
                Assert.That(retained!.LocalContentHash, Is.EqualTo(baselineRemote.ContentHash));
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });

            SyncRunResult repeated = await engine.RunOnceAsync(Pair(), new SyncRunOptions
            {
                Scope = SyncRunScope.ForLocalChangedPaths(result.DeferredLocalPaths),
            });

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("modified"));
                Assert.That(repeated.HasDeferredLocalPaths, Is.False);
                Assert.That(repeated.Activities.Select(activity => activity.Kind), Does.Contain(SyncActivityKind.Conflict));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });
        }
    }
}
