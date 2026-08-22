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
        public async Task RunOnceAsync_WritesStructuredStartAndCompletionLogs()
        {
            RecordingLogger<SyncEngine> logger = new RecordingLogger<SyncEngine>();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out _,
                logger: logger);

            await engine.RunOnceAsync(Pair());

            Assert.Multiple(() =>
            {
                Assert.That(logger.Entries.Select(entry => entry.Level), Is.EqualTo(new[] { LogLevel.Information, LogLevel.Information }));
                Assert.That(logger.Entries[0].Message, Does.Contain("Starting sync pass for pair pair-a"));
                Assert.That(logger.Entries[1].Message, Does.Contain("Completed sync pass for pair pair-a with 0 activities"));
            });
        }


        [Test]
        public async Task RunOnceAsync_LoadsBaselineThroughStreamingStateApi()
        {
            StreamingOnlyStateStore stateStore = new StreamingOnlyStateStore(new SqliteSyncStateStore(_databasePath));
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                new FakeRemoteFileSynchronizer(),
                stateStore);

            await engine.RunOnceAsync(Pair());

            Assert.That(stateStore.LoadPairEntriesCallCount, Is.EqualTo(1));
        }


        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-sync-engine", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, ".cotton-sync", "state.sqlite");
        }


        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }


        [Test]
        public async Task RunOnceAsync_UploadsLocalOnlyFileAndStoresBaseline()
        {
            LocalFileSnapshot local = LocalFile("Docs/local.txt", "local-content");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            List<SyncActivity> progress = new List<SyncActivity>();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { ActivityProgress = new Progress<SyncActivity>(progress.Add) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "docs/LOCAL.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo("Docs/local.txt"));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile, Is.Null);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(progress.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remoteFiles.Uploads[0].ReturnedFile.Id));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadsLocalOnlyFileAndStoresBaseline()
        {
            LocalFileSnapshot local = LocalFile("Docs/local-created.txt", "local-created-content");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "Docs/local-created.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo("Docs/local-created.txt"));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile, Is.Null);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remoteFiles.Uploads[0].ReturnedFile.Id));
            });
        }


        [Test]
        public async Task RunOnceAsync_UploadsLocalOnlyMetadataSnapshotAfterLazyHashing()
        {
            LocalFileSnapshot local = new LocalFileSnapshot
            {
                RelativePath = "Docs/large.bin",
                FullPath = Path.Combine(_root, "Docs", "large.bin"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 6, 8, 0, 0, DateTimeKind.Utc),
            };
            MetadataOnlyLocalFileScanner scanner = new MetadataOnlyLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "docs/large.bin");
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.EqualTo(1));
                Assert.That(remoteFiles.UploadInputContentHashes, Is.EqualTo(new[] { "precomputed-content-hash" }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo("precomputed-content-hash"));
                Assert.That(entry.RemoteContentHash, Is.EqualTo("precomputed-content-hash"));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesReportsMatchingMaterializedFileForFinalization()
        {
            const string relativePath = "Docs/already-uploaded.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "matching-content");
            NodeFileManifestDto remote = RemoteFile(
                relativePath,
                local.ContentHash,
                sizeBytes: local.SizeBytes);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                RemoteTree(remote),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Converged }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_ReusesMatchingRemoteFileAfterCreateConflictWithoutFullRecoveryCrawl()
        {
            const string relativePath = "Docs/local.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "local-content");
            NodeFileManifestDto committedRemote = RemoteFile(
                relativePath,
                local.ContentHash,
                sizeBytes: local.SizeBytes);
            FakeLocalFileScanner scanner = new(local);
            FakeRemoteFileSynchronizer remoteFiles = new();
            remoteFiles.CreateConflictRelativePaths.Add(relativePath);
            FakeRemoteTreeCrawler remoteCrawler = new(EmptyRemoteTree(), RemoteTree(committedRemote));
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, remoteCrawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteCrawler.CrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(committedRemote.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(committedRemote.Id));
            });
        }


        [Test]
        public async Task RunOnceAsync_UsesMetadataLookupScannerWhenAvailable()
        {
            LocalFileSnapshot local = new LocalFileSnapshot
            {
                RelativePath = "Docs/direct-lookup.bin",
                FullPath = Path.Combine(_root, "Docs", "direct-lookup.bin"),
                ContentHash = string.Empty,
                SizeBytes = 2048,
                LastWriteUtc = new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
            };
            LookupOnlyLocalFileScanner scanner = new LookupOnlyLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "docs/direct-lookup.bin");
            Assert.Multiple(() =>
            {
                Assert.That(scanner.LookupScanCalls, Is.EqualTo(1));
                Assert.That(scanner.MetadataTreeScanCalls, Is.Zero);
                Assert.That(scanner.TreeScanCalls, Is.Zero);
                Assert.That(remoteFiles.UploadInputContentHashes, Is.EqualTo(new[] { "precomputed-content-hash" }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo("precomputed-content-hash"));
                Assert.That(entry.LocalSizeBytes, Is.EqualTo(local.SizeBytes));
            });
        }


        [Test]
        public async Task RunOnceAsync_UsesRemoteLookupCrawlerWhenAvailable()
        {
            FakeLocalFileScanner scanner = new FakeLocalFileScanner();
            LookupOnlyRemoteTreeCrawler crawler = new LookupOnlyRemoteTreeCrawler(EmptyRemoteTree());
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new(scanner, crawler, new FakeRemoteFileSynchronizer(), stateStore);

            await engine.RunOnceAsync(Pair());

            Assert.Multiple(() =>
            {
                Assert.That(crawler.LookupCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.ProgressCrawlCalls, Is.Zero);
                Assert.That(crawler.SnapshotCrawlCalls, Is.Zero);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithLocalChangedPathUsesScopedScanners()
        {
            WriteFile("changed.txt", "local");
            LocalFileScanner scanner = new LocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(EmptyRemoteTree());
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer
            {
                EmptyLocalHashUploadContentHash = "uploaded-content-hash",
            };
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths(["changed.txt"]) });

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { "changed.txt" }));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithNestedLocalChangedFileDoesNotScanSiblingFiles()
        {
            WriteFile("Project/changed.txt", "local");
            WriteFile("Project/sibling.txt", "sibling");
            LocalFileScanner scanner = new LocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(EmptyRemoteTree());
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer
            {
                EmptyLocalHashUploadContentHash = "uploaded-content-hash",
            };
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths(["Project/changed.txt"]) });

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { "Project/changed.txt" }));
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EqualTo(new[] { "Project/changed.txt" }));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
            });
        }
    }
}
