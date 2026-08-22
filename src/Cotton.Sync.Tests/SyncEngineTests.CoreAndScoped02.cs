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
        public async Task RunOnceAsync_WithScopedLocalDeletedPathDeletesRemoteWithoutFullCrawl()
        {
            string relativePath = "Project/deleted.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            LocalFileScanner scanner = new LocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(stateStore, relativePath, remote.ContentHash, remote);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedLocalRenamePathsUploadsNewAndDeletesOldWithoutFullCrawl()
        {
            string oldPath = "Project/old-name.txt";
            string newPath = "Project/new-name.txt";
            NodeFileManifestDto oldRemote = RemoteFile(oldPath, HashText("old"));
            WriteFile(newPath, "new");
            string newContentHash = HashText("new");
            LocalFileScanner scanner = new LocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(oldRemote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer
            {
                EmptyLocalHashUploadContentHash = newContentHash,
            };
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(stateStore, oldPath, oldRemote.ContentHash, oldRemote);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath]) });

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (oldRemote.Id, false, oldRemote.ETag) }));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EquivalentTo(new[] { oldPath, newPath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EquivalentTo(new[] { SyncActivityKind.DeletedRemote, SyncActivityKind.Uploaded }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WhenLazyHashFileIsUnavailableContinuesWithOtherFiles()
        {
            LocalFileSnapshot unavailable = LocalFile("Docs/unavailable.bin", "unavailable");
            unavailable.ContentHash = string.Empty;
            LocalFileSnapshot available = LocalFile("Docs/available.bin", "available");
            available.ContentHash = string.Empty;
            FakeLocalFileScanner scanner = new(unavailable, available)
            {
                ContentHashFactory = file =>
                {
                    if (file.RelativePath == unavailable.RelativePath)
                    {
                        throw new LocalFileUnavailableException(
                            file.RelativePath,
                            file.FullPath,
                            "the file is locked.",
                            requiresExclusiveAccess: true);
                    }

                    return "available-content-hash";
                },
            };
            FakeRemoteFileSynchronizer remoteFiles = new();
            NodeFileManifestDto unavailableRemote = RemoteFile(
                unavailable.RelativePath,
                "baseline-content-hash",
                sizeBytes: unavailable.SizeBytes);
            SyncEngine engine = CreateEngine(
                scanner,
                RemoteTree(unavailableRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(
                stateStore,
                unavailable.RelativePath,
                "baseline-content-hash",
                unavailableRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncActivity unavailableActivity = result.Activities.Single(activity =>
                activity.RelativePath == unavailable.RelativePath);
            SyncStateEntry? unavailableEntry = await stateStore.GetAsync("pair-a", unavailable.RelativePath);
            SyncStateEntry? availableEntry = await stateStore.GetAsync("pair-a", available.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(unavailableActivity.Kind, Is.EqualTo(SyncActivityKind.Skipped));
                Assert.That(unavailableActivity.Details, Does.Contain("locked"));
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { unavailable.RelativePath }));
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EqualTo(new[] { available.RelativePath }));
                Assert.That(unavailableEntry, Is.Not.Null);
                Assert.That(unavailableEntry!.LocalContentHash, Is.EqualTo("baseline-content-hash"));
                Assert.That(availableEntry, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedRapidRenameEditDeleteDeletesOldRemoteWithoutStaleTarget()
        {
            const string oldPath = "old.txt";
            const string newPath = "new.txt";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("created-content"));
            FakeLocalFileScanner scanner = new();
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertBaselineAsync(stateStore, oldPath, remote.ContentHash, remote);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath], [newPath]),
                });

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { oldPath }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Null);
            });
        }
    }
}
