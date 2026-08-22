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
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRepairsPersistedPlaceholderBaselineWithoutIdentity()
        {
            const string relativePath = "interrupted-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteSizeBytes = remote.SizeBytes,
                RemoteFileId = remote.Id,
                RemoteNodeId = remote.NodeId,
                RemoteFileManifestId = remote.FileManifestId,
                RemoteOriginalNodeFileId = remote.OriginalNodeFileId,
                RemoteContentHash = remote.ContentHash,
                RemoteETag = remote.ETag,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = DateTime.UtcNow,
            });
            SyncEngine engine = new SyncEngine(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.Zero);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesFolderChangeDoesNotExpandLocalDirectoryTarget()
        {
            const string relativePath = "LargeTree";
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(relativePath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner();
            scanner.Directories.Add(new LocalDirectorySnapshot
            {
                RelativePath = relativePath,
                FullPath = Path.Combine(_root, relativePath),
            });
            scanner.Files.Add(LocalFile("LargeTree/Child/placeholder.txt", "placeholder-content"));
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            Assert.Multiple(() =>
            {
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(scanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(scanner.LastIncludeDirectoryDescendants, Is.False);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesFolderChangeDoesNotPlanRemoteDeletesForUnscannedMaterializedDescendants()
        {
            const string relativePath = "Music";
            NodeFileManifestDto firstRemote = RemoteFile("Music/Album/one.mp3", HashText("one"), sizeBytes: 3);
            NodeFileManifestDto secondRemote = RemoteFile("Music/Album/two.mp3", HashText("two"), sizeBytes: 3);
            RemoteDirectorySnapshot musicDirectory = RemoteDirectory(relativePath);
            RemoteDirectorySnapshot albumDirectory = RemoteDirectory("Music/Album", musicDirectory.Node.Id);
            RemoteTreeSnapshot remoteTree = RemoteTree(firstRemote, secondRemote);
            remoteTree.Directories.Add(musicDirectory);
            remoteTree.Directories.Add(albumDirectory);
            FakeLocalFileScanner scanner = new();
            scanner.Directories.Add(new LocalDirectorySnapshot
            {
                RelativePath = relativePath,
                FullPath = Path.Combine(_root, relativePath),
            });
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertBaselineAsync(stateStore, "Music/Album/one.mp3", firstRemote.ContentHash, firstRemote, firstRemote.SizeBytes);
            await InsertBaselineAsync(stateStore, "Music/Album/two.mp3", secondRemote.ContentHash, secondRemote, secondRemote.SizeBytes);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    Scope = SyncRunScope.ForLocalChangedPaths([relativePath]),
                });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", "Music/Album/one.mp3");
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", "Music/Album/two.mp3");
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(scanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(scanner.LastIncludeDirectoryDescendants, Is.False);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Does.Not.Contain(SyncActivityKind.DeletedRemote));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.False);
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesLocalAddsDoNotPlanMassRemoteDeletesForUnscannedLargeTree()
        {
            const string directoryPath = "Music";
            const int existingFileCount = 2_207;
            string[] newPaths = ["Music/new-one.mp3", "Music/new-two.mp3"];
            RemoteDirectorySnapshot musicDirectory = RemoteDirectory(directoryPath);
            RemoteDirectorySnapshot albumDirectory = RemoteDirectory("Music/Album", musicDirectory.Node.Id);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(musicDirectory);
            remoteTree.Directories.Add(albumDirectory);
            List<SyncStateEntry> baselineEntries =
            [
                new SyncStateEntry
                {
                    SyncPairId = "pair-a",
                    RelativePath = directoryPath,
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = musicDirectory.Node.Id,
                    SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
                },
                new SyncStateEntry
                {
                    SyncPairId = "pair-a",
                    RelativePath = albumDirectory.RelativePath,
                    Kind = SyncEntryKind.Directory,
                    RemoteNodeId = albumDirectory.Node.Id,
                    SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
                },
            ];
            for (int index = 0; index < existingFileCount; index++)
            {
                string relativePath = $"Music/Album/track-{index:D4}.mp3";
                string contentHash = HashText($"track-{index:D4}");
                NodeFileManifestDto remoteFile = RemoteFile(relativePath, contentHash, sizeBytes: 10);
                remoteTree.Files.Add(new RemoteFileSnapshot
                {
                    RelativePath = relativePath,
                    File = remoteFile,
                });
                baselineEntries.Add(new SyncStateEntry
                {
                    SyncPairId = "pair-a",
                    RelativePath = relativePath,
                    Kind = SyncEntryKind.File,
                    LocalContentHash = contentHash,
                    LocalLastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
                    LocalSizeBytes = remoteFile.SizeBytes,
                    RemoteNodeId = remoteFile.NodeId,
                    RemoteFileId = remoteFile.Id,
                    RemoteContentHash = remoteFile.ContentHash,
                    RemoteETag = remoteFile.ETag,
                    SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
                });
            }

            FakeLocalFileScanner scanner = new(
                LocalFile(newPaths[0], "new-one"),
                LocalFile(newPaths[1], "new-two"));
            scanner.Directories.Add(new LocalDirectorySnapshot
            {
                RelativePath = directoryPath,
                FullPath = Path.Combine(_root, directoryPath),
            });
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await stateStore.InitializeAsync();
            await stateStore.ReplacePairAsync("pair-a", baselineEntries);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 100,
                    Scope = SyncRunScope.ForLocalChangedPaths([directoryPath, .. newPaths]),
                });

            IReadOnlyList<SyncStateEntry> finalEntries = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(scanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(scanner.LastIncludeDirectoryDescendants, Is.False);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EquivalentTo(newPaths));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.All.EqualTo(SyncActivityKind.Uploaded));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EquivalentTo(newPaths));
                Assert.That(finalEntries, Has.Count.EqualTo(existingFileCount + 2 + newPaths.Length));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesPreservesRemoteOnlyPlaceholderStateAfterEngineRestart()
        {
            const string relativePath = "remote-only-restart.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter firstPlaceholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore firstStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstEngine = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                firstStateStore,
                remoteFilePlaceholderWriter: firstPlaceholderWriter);

            SyncRunResult firstResult = await firstEngine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            FakeRemoteFilePlaceholderWriter restartedPlaceholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore restartedStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine restartedEngine = new(
                new LocalFileScanner(),
                new PathOnlyRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                restartedStateStore,
                remoteFilePlaceholderWriter: restartedPlaceholderWriter);
            SyncRunResult restartedResult = await restartedEngine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await restartedStateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(firstResult.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(firstPlaceholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(restartedResult.Activities, Is.Empty);
                Assert.That(restartedResult.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(restartedPlaceholderWriter.Requests, Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(firstPlaceholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }
    }
}
