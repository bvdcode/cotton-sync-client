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
    public class SyncEngineTests
    {
        private readonly Guid _remoteRootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private string _root = string.Empty;
        private string _databasePath = string.Empty;

        public enum MatrixFileState
        {
            Missing,
            Baseline,
            Changed,
        }

        [Test]
        public async Task RunOnceAsync_WritesStructuredStartAndCompletionLogs()
        {
            var logger = new RecordingLogger<SyncEngine>();
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
            var stateStore = new StreamingOnlyStateStore(new SqliteSyncStateStore(_databasePath));
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
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var progress = new List<SyncActivity>();
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
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
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
            var local = new LocalFileSnapshot
            {
                RelativePath = "Docs/large.bin",
                FullPath = Path.Combine(_root, "Docs", "large.bin"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 6, 8, 0, 0, DateTimeKind.Utc),
            };
            var scanner = new MetadataOnlyLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
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
            var local = new LocalFileSnapshot
            {
                RelativePath = "Docs/direct-lookup.bin",
                FullPath = Path.Combine(_root, "Docs", "direct-lookup.bin"),
                ContentHash = string.Empty,
                SizeBytes = 2048,
                LastWriteUtc = new DateTime(2026, 6, 6, 9, 0, 0, DateTimeKind.Utc),
            };
            var scanner = new LookupOnlyLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
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
            var scanner = new FakeLocalFileScanner();
            var crawler = new LookupOnlyRemoteTreeCrawler(EmptyRemoteTree());
            var stateStore = new SqliteSyncStateStore(_databasePath);
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
            var scanner = new LocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(EmptyRemoteTree());
            var remoteFiles = new FakeRemoteFileSynchronizer
            {
                EmptyLocalHashUploadContentHash = "uploaded-content-hash",
            };
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

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
            var scanner = new LocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(EmptyRemoteTree());
            var remoteFiles = new FakeRemoteFileSynchronizer
            {
                EmptyLocalHashUploadContentHash = "uploaded-content-hash",
            };
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

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

        [Test]
        public async Task RunOnceAsync_WithScopedLocalDeletedPathDeletesRemoteWithoutFullCrawl()
        {
            string relativePath = "Project/deleted.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            var scanner = new LocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(stateStore, relativePath, remote.ContentHash, remote);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

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
            var scanner = new LocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(oldRemote));
            var remoteFiles = new FakeRemoteFileSynchronizer
            {
                EmptyLocalHashUploadContentHash = newContentHash,
            };
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(stateStore, oldPath, oldRemote.ContentHash, oldRemote);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

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

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRemoteOnlyPlaceholderChurnDoesNotRequireAction()
        {
            const string relativePath = "remote-only.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            var scanner = new LocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore, remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesLocalDeletedRemoteOnlyPlaceholderDeletesRemote()
        {
            const string relativePath = "remote-only-deleted.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new();
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath], [relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesLocalDeletedRemoteOnlyPlaceholdersHonorsRemoteDeleteLimit()
        {
            const string firstPath = "remote-only-deleted-a.txt";
            const string secondPath = "remote-only-deleted-b.txt";
            NodeFileManifestDto firstRemote = RemoteFile(firstPath, HashText("remote-a"), sizeBytes: 1024);
            NodeFileManifestDto secondRemote = RemoteFile(secondPath, HashText("remote-b"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new();
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(firstRemote, secondRemote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, firstPath, firstRemote);
            await InsertPlaceholderBaselineAsync(stateStore, secondPath, secondRemote);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult blockedResult = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    Scope = SyncRunScope.ForLocalChangedPaths([firstPath, secondPath], [firstPath, secondPath]),
                });

            SyncRunResult changedPlanResult = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    ApprovedRemoteDeleteCount = 3,
                    Scope = SyncRunScope.ForLocalChangedPaths([firstPath, secondPath], [firstPath, secondPath]),
                });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", firstPath);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", secondPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(blockedResult.RequiresUserAction, Is.True);
                Assert.That(changedPlanResult.RequiresUserAction, Is.True);
                Assert.That(blockedResult.Activities.Select(activity => activity.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(blockedResult.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(blockedResult.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(blockedResult.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });

            SyncRunResult approvedResult = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    ApprovedRemoteDeleteCount = 2,
                    Scope = SyncRunScope.ForLocalChangedPaths([firstPath, secondPath], [firstPath, secondPath]),
                });

            firstEntry = await stateStore.GetAsync("pair-a", firstPath);
            secondEntry = await stateStore.GetAsync("pair-a", secondPath);
            Assert.Multiple(() =>
            {
                Assert.That(approvedResult.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.Deletes, Has.Count.EqualTo(2));
                Assert.That(firstEntry, Is.Null);
                Assert.That(secondEntry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRemoteCreateUsesPathLookupAndCreatesPlaceholder()
        {
            const string relativePath = "remote-created.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            var scanner = new FakeLocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(
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
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRepairsPlaceholderWhoseBaselineWasInterrupted()
        {
            const string relativePath = "interrupted-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt;
            var scanner = new FakeLocalFileScanner(local);
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(
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
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRefreshesInterruptedPlaceholderMetadataWithoutHashing()
        {
            const string relativePath = "interrupted-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt.AddMinutes(-5);
            var scanner = new FakeLocalFileScanner(local);
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(
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
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRepairsPersistedPlaceholderBaselineWithoutIdentity()
        {
            const string relativePath = "interrupted-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt;
            var scanner = new FakeLocalFileScanner(local);
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
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
            var engine = new SyncEngine(
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
            var scanner = new FakeLocalFileScanner();
            scanner.Directories.Add(new LocalDirectorySnapshot
            {
                RelativePath = relativePath,
                FullPath = Path.Combine(_root, relativePath),
            });
            scanner.Files.Add(LocalFile("LargeTree/Child/placeholder.txt", "placeholder-content"));
            var crawler = new PathOnlyRemoteTreeCrawler(remoteTree);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);

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
                    Scope = SyncRunScope.ForLocalChangedPaths([directoryPath, ..newPaths]),
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
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var firstPlaceholderWriter = new FakeRemoteFilePlaceholderWriter();
            var firstStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstEngine = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                firstStateStore,
                remoteFilePlaceholderWriter: firstPlaceholderWriter);

            SyncRunResult firstResult = await firstEngine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            var restartedPlaceholderWriter = new FakeRemoteFilePlaceholderWriter();
            var restartedStateStore = new SqliteSyncStateStore(_databasePath);
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

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesHydratedEditUploadsNormally()
        {
            const string relativePath = "hydrated-edited.txt";
            string oldHash = HashText("old-content");
            WriteFile(relativePath, "local-new-content");
            NodeFileManifestDto remote = RemoteFile(relativePath, oldHash, sizeBytes: Encoding.UTF8.GetByteCount("old-content"));
            var scanner = new LocalFileScanner();
            var crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore);
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

        [Test]
        public async Task RunOnceAsync_MovesRemoteFileWhenLocalPathChangesWithoutContentChange()
        {
            string oldPath = "Project/old-name.txt";
            string newPath = "Project/new-name.txt";
            string content = "same-content";
            WriteFile(newPath, content);
            LocalFileSnapshot local = LocalFile(newPath, content);
            NodeFileManifestDto oldRemote = RemoteFile(oldPath, local.ContentHash, sizeBytes: local.SizeBytes);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                RemoteTree(oldRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, oldPath, local.ContentHash, oldRemote, local.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves.Select(move => move.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(remoteFiles.Moves[0].ExistingRemoteFile.Id, Is.EqualTo(oldRemote.Id));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Moved }));
                Assert.That(result.Activities.Select(activity => activity.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Not.Null);
                Assert.That(newEntry!.RemoteFileId, Is.EqualTo(oldRemote.Id));
                Assert.That(newEntry.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(newEntry.LocalSizeBytes, Is.EqualTo(local.SizeBytes));
            });
        }

        [Test]
        public async Task RunOnceAsync_LocalMovesDoNotTripRemoteMassDeleteGuard()
        {
            LocalFileSnapshot firstLocal = LocalFile("moved-a.txt", "content-a");
            LocalFileSnapshot secondLocal = LocalFile("moved-b.txt", "content-b");
            NodeFileManifestDto firstRemote = RemoteFile("a.txt", firstLocal.ContentHash, sizeBytes: firstLocal.SizeBytes);
            NodeFileManifestDto secondRemote = RemoteFile("b.txt", secondLocal.ContentHash, sizeBytes: secondLocal.SizeBytes);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(firstLocal, secondLocal),
                RemoteTree(firstRemote, secondRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "a.txt", firstLocal.ContentHash, firstRemote, firstLocal.SizeBytes);
            await InsertBaselineAsync(stateStore, "b.txt", secondLocal.ContentHash, secondRemote, secondLocal.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            SyncStateEntry? firstOldEntry = await stateStore.GetAsync("pair-a", "a.txt");
            SyncStateEntry? secondOldEntry = await stateStore.GetAsync("pair-a", "b.txt");
            SyncStateEntry? firstNewEntry = await stateStore.GetAsync("pair-a", "moved-a.txt");
            SyncStateEntry? secondNewEntry = await stateStore.GetAsync("pair-a", "moved-b.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves.Select(move => move.RelativePath), Is.EquivalentTo(new[] { "moved-a.txt", "moved-b.txt" }));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.All.EqualTo(SyncActivityKind.Moved));
                Assert.That(firstOldEntry, Is.Null);
                Assert.That(secondOldEntry, Is.Null);
                Assert.That(firstNewEntry, Is.Not.Null);
                Assert.That(secondNewEntry, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_MovePreconditionFailureFallsBackToConflictAndUploadWithoutDeletingRemote()
        {
            string oldPath = "Project/old-name.txt";
            string newPath = "Project/new-name.txt";
            string localContent = "same-content";
            WriteFile(newPath, localContent);
            LocalFileSnapshot local = LocalFile(newPath, localContent);
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto oldRemote = RemoteFile(oldPath, local.ContentHash, remoteId, local.SizeBytes);
            byte[] latestRemoteContent = Encoding.UTF8.GetBytes("remote-changed");
            NodeFileManifestDto latestRemote = RemoteFile(oldPath, Hash(latestRemoteContent), remoteId, latestRemoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedMoveIds.Add(remoteId);
            remoteFiles.Downloads[remoteId] = latestRemoteContent;
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(oldRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, oldPath, local.ContentHash, oldRemote, local.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(File.ReadAllText(Path.Combine(_root, oldPath.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo("remote-changed"));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EquivalentTo(new[] { SyncActivityKind.Conflict, SyncActivityKind.Uploaded }));
                Assert.That(oldEntry, Is.Not.Null);
                Assert.That(newEntry, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_HashesMetadataSnapshotWhenBaselineNeedsComparison()
        {
            const string baselineHash = "precomputed-content-hash";
            var local = new LocalFileSnapshot
            {
                RelativePath = "Docs/existing.bin",
                FullPath = Path.Combine(_root, "Docs", "existing.bin"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 6, 8, 0, 0, DateTimeKind.Utc),
            };
            var scanner = new MetadataOnlyLocalFileScanner(local);
            NodeFileManifestDto remote = RemoteFile("Docs/existing.bin", baselineHash, sizeBytes: local.SizeBytes);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "Docs/existing.bin", baselineHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.EqualTo(1));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_ReusesBaselineHashWhenMetadataIsUnchanged()
        {
            const string baselineHash = "existing-content-hash";
            var baselineSyncedAtUtc = new DateTime(2026, 6, 6, 8, 1, 0, DateTimeKind.Utc);
            var local = new LocalFileSnapshot
            {
                RelativePath = "Docs/existing.bin",
                FullPath = Path.Combine(_root, "Docs", "existing.bin"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 6, 8, 0, 0, DateTimeKind.Utc),
            };
            var scanner = new MetadataOnlyLocalFileScanner(local);
            NodeFileManifestDto remote = RemoteFile("Docs/existing.bin", baselineHash, sizeBytes: local.SizeBytes);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = local.RelativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = baselineHash,
                LocalLastWriteUtc = local.LastWriteUtc,
                LocalSizeBytes = local.SizeBytes,
                RemoteNodeId = remote.NodeId,
                RemoteFileId = remote.Id,
                RemoteContentHash = remote.ContentHash,
                RemoteETag = remote.ETag,
                SyncedAtUtc = baselineSyncedAtUtc,
            });

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", local.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.Zero);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(local.ContentHash, Is.EqualTo(baselineHash));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalSizeBytes, Is.EqualTo(local.SizeBytes));
                Assert.That(entry.SyncedAtUtc, Is.EqualTo(baselineSyncedAtUtc));
            });
        }

        [Test]
        public async Task RunOnceAsync_ReportsAggregateRunProgressFileCounts()
        {
            var scanner = new FakeLocalFileScanner(
                LocalFile("Docs/a.txt", "a"),
                LocalFile("Docs/b.txt", "b"));
            var progress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = progress });

            IReadOnlyList<SyncRunProgress> fileProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.ReconcilingFiles)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(progress.Values[0].Stage, Is.EqualTo(SyncRunProgressStage.ScanningLocal));
                Assert.That(progress.Values.Any(item => item.Stage == SyncRunProgressStage.ScanningRemote), Is.True);
                Assert.That(progress.Values.Any(item => item.Stage == SyncRunProgressStage.ReconcilingDirectories), Is.True);
                Assert.That(fileProgress.Select(item => item.FilesTotal).Distinct(), Is.EqualTo(new int?[] { 2 }));
                Assert.That(fileProgress.Select(item => item.FilesCompleted).Distinct(), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(fileProgress.Where(item => !string.IsNullOrWhiteSpace(item.CurrentPath)).Select(item => item.CurrentPath).Distinct(), Is.EqualTo(new[] { "Docs/a.txt", "Docs/b.txt" }));
                Assert.That(progress.Values[^1].Stage, Is.EqualTo(SyncRunProgressStage.Completed));
                Assert.That(progress.Values[^1].FilesCompleted, Is.EqualTo(2));
                Assert.That(progress.Values[^1].FilesTotal, Is.EqualTo(2));
                Assert.That(progress.Values[^1].IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_ReportsLocalScanFileDiscoveryProgress()
        {
            var scanner = new MetadataOnlyLocalFileScanner(
                LocalFile("Docs/a.txt", "a"),
                LocalFile("Docs/b.txt", "b"))
            {
                ReportMetadataScanProgress = true,
            };
            var progress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = progress });

            int remoteScanIndex = progress.Values
                .Select((item, index) => (item, index))
                .First(item => item.item.Stage == SyncRunProgressStage.ScanningRemote)
                .index;
            IReadOnlyList<SyncRunProgress> localScanProgress = progress.Values
                .Take(remoteScanIndex)
                .Where(item => item.Stage == SyncRunProgressStage.ScanningLocal)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(localScanProgress.Select(item => item.FilesCompleted), Does.Contain(1));
                Assert.That(localScanProgress.Select(item => item.FilesCompleted), Does.Contain(2));
                Assert.That(localScanProgress.Where(item => !string.IsNullOrWhiteSpace(item.CurrentPath)).Select(item => item.CurrentPath), Is.EqualTo(new[] { "Docs/a.txt", "Docs/b.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_ReportsRemoteScanFileDiscoveryProgress()
        {
            var progress = new RecordingProgress<SyncRunProgress>();
            var remoteCrawler = new FakeRemoteTreeProgressCrawler(
                EmptyRemoteTree(),
                "Cloud/a.txt",
                "Cloud/b.txt");
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                remoteCrawler,
                new FakeRemoteFileSynchronizer(),
                new SqliteSyncStateStore(_databasePath));

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = progress });

            IReadOnlyList<SyncRunProgress> remoteScanProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.ScanningRemote)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(remoteScanProgress.Select(item => item.FilesCompleted), Does.Contain(1));
                Assert.That(remoteScanProgress.Select(item => item.FilesCompleted), Does.Contain(2));
                Assert.That(remoteScanProgress.Select(item => item.FilesTotal), Does.Contain(2));
                Assert.That(remoteScanProgress.Where(item => !string.IsNullOrWhiteSpace(item.CurrentPath)).Select(item => item.CurrentPath), Is.EqualTo(new[] { "Cloud/a.txt", "Cloud/b.txt" }));
            });
        }

        [Test]
        public void RunOnceAsync_WithScopedRequestRejectsMissingPathLookupCapabilities()
        {
            MetadataOnlyLocalFileScanner scanner = new();
            LookupOnlyRemoteTreeCrawler crawler = new(EmptyRemoteTree());
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths(["changed.txt"]) }));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("Scoped sync requires"));
                Assert.That(crawler.LookupCrawlCalls, Is.Zero);
                Assert.That(crawler.ProgressCrawlCalls, Is.Zero);
                Assert.That(crawler.SnapshotCrawlCalls, Is.Zero);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedFileChangeDoesNotDeleteImplicitRemoteParentDirectory()
        {
            const string parentPath = "Recordings/Calls";
            const string relativePath = "Recordings/Calls/subtitle.srt";
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(parentPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            FakeLocalFileScanner scanner = new(LocalFile(relativePath, "subtitle"));
            PathOnlyRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new()
            {
                EmptyLocalHashUploadContentHash = HashText("subtitle"),
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, parentPath, remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? parentState = await stateStore.GetAsync("pair-a", parentPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(parentState, Is.Not.Null);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedRemoteFolderDeletePreservesUnobservedLocalChildren()
        {
            const string relativePath = "Projects";
            WriteFile("Projects/local.txt", "local");
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(relativePath);
            FakeLocalFileScanner scanner = new()
            {
                Directories =
                {
                    LocalDirectory(relativePath),
                },
            };
            PathOnlyRemoteTreeCrawler crawler = new(EmptyRemoteTree());
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);
            await InsertDirectoryBaselineAsync(stateStore, relativePath, remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? directoryState = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, relativePath)), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "Projects", "local.txt")), Is.True);
                Assert.That(directoryState, Is.Not.Null);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped }));
                Assert.That(result.Activities[0].Details, Does.Contain("not empty"));
            });
        }

        [Test]
        public async Task RunOnceAsync_ReportsDirectoryReconcileProgressWithFolderCounts()
        {
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            var progress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out _,
                new FakeRemoteDirectorySynchronizer());

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = progress });

            IReadOnlyList<SyncRunProgress> directoryProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.ReconcilingDirectories)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(directoryProgress.Select(item => item.FilesTotal).Distinct(), Is.EqualTo(new int?[] { 2 }));
                Assert.That(directoryProgress.Select(item => item.FilesCompleted).Distinct(), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(
                    directoryProgress.Where(item => !string.IsNullOrWhiteSpace(item.CurrentPath)).Select(item => item.CurrentPath).Distinct(),
                    Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_ThrottlesLargeFileReconcileProgress()
        {
            const int fileCount = 250;
            var locals = new List<LocalFileSnapshot>();
            var remotes = new List<NodeFileManifestDto>();
            for (int index = 0; index < fileCount; index++)
            {
                string path = "Docs/file-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                string content = "content-" + index.ToString(CultureInfo.InvariantCulture);
                LocalFileSnapshot local = LocalFile(path, content);
                NodeFileManifestDto remote = RemoteFile(path, local.ContentHash, sizeBytes: local.SizeBytes);
                locals.Add(local);
                remotes.Add(remote);
            }

            var progress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(locals.ToArray()), RemoteTree(remotes.ToArray()), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            for (int index = 0; index < fileCount; index++)
            {
                await InsertBaselineAsync(stateStore, locals[index].RelativePath, locals[index].ContentHash, remotes[index]);
            }

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = progress });

            IReadOnlyList<SyncRunProgress> fileProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.ReconcilingFiles)
                .ToList();
            int[] completedCounts = fileProgress
                .Select(static item => item.FilesCompleted)
                .Distinct()
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(fileProgress, Has.Count.LessThan(fileCount));
                Assert.That(completedCounts[0], Is.EqualTo(0));
                Assert.That(completedCounts, Does.Contain(25));
                Assert.That(completedCounts[^1], Is.EqualTo(fileCount));
            });
        }

        [Test]
        public async Task RunOnceAsync_ThrottlesLargeDirectoryReconcileProgress()
        {
            const int directoryCount = 250;
            var scanner = new FakeLocalFileScanner();
            for (int index = 0; index < directoryCount; index++)
            {
                scanner.Directories.Add(LocalDirectory("Folder-" + index.ToString("000", CultureInfo.InvariantCulture)));
            }

            var progress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out _,
                new FakeRemoteDirectorySynchronizer());

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = progress });

            IReadOnlyList<SyncRunProgress> directoryProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.ReconcilingDirectories)
                .ToList();
            int[] completedCounts = directoryProgress
                .Select(static item => item.FilesCompleted)
                .Distinct()
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(directoryProgress, Has.Count.LessThan(directoryCount));
                Assert.That(completedCounts[0], Is.EqualTo(0));
                Assert.That(completedCounts, Does.Contain(25));
                Assert.That(completedCounts[^1], Is.EqualTo(directoryCount));
            });
        }

        [Test]
        public async Task RunOnceAsync_ReportsRunTransferAndActivityProgressForUpload()
        {
            LocalFileSnapshot local = LocalFile("Docs/local.txt", "local-content");
            var eventLog = new List<string>();
            var runProgress = new RecordingProgress<SyncRunProgress>(
                item => eventLog.Add($"run:{item.Stage}:{item.FilesCompleted}:{item.CurrentPath}:{item.IsCompleted}"));
            var transferProgress = new RecordingProgress<SyncTransferProgress>(
                item => eventLog.Add($"transfer:{item.Direction}:{item.RelativePath}:{item.TransferredBytes}:{item.TotalBytes}:{item.IsCompleted}"));
            var activityProgress = new RecordingProgress<SyncActivity>(
                item => eventLog.Add($"activity:{item.Kind}:{item.RelativePath}"));
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions
                {
                    ActivityProgress = activityProgress,
                    TransferProgress = transferProgress,
                    RunProgress = runProgress,
                });

            int fileStartedIndex = eventLog.FindIndex(item => item.StartsWith("run:ReconcilingFiles:0:Docs/local.txt:", StringComparison.Ordinal));
            int transferStartedIndex = eventLog.FindIndex(item => item == $"transfer:Upload:Docs/local.txt:0:{local.SizeBytes}:False");
            int transferCompletedIndex = eventLog.FindIndex(item => item == $"transfer:Upload:Docs/local.txt:{local.SizeBytes}:{local.SizeBytes}:True");
            int activityIndex = eventLog.FindIndex(item => item == "activity:Uploaded:Docs/local.txt");
            int runCompletedIndex = eventLog.FindIndex(item => item == "run:Completed:1::True");
            SyncRunProgress? fileStartProgress = runProgress.Values.FirstOrDefault(item =>
                item.Stage == SyncRunProgressStage.ReconcilingFiles && item.FilesCompleted == 0);
            SyncRunProgress? completedProgress = runProgress.Values.FirstOrDefault(item => item.Stage == SyncRunProgressStage.Completed);
            Assert.Multiple(() =>
            {
                Assert.That(runProgress.Values.Select(item => item.Stage), Does.Contain(SyncRunProgressStage.Completed));
                Assert.That(fileStartProgress, Is.Not.Null);
                Assert.That(fileStartProgress!.BytesCompleted, Is.Zero);
                Assert.That(fileStartProgress.BytesTotal, Is.EqualTo(local.SizeBytes));
                Assert.That(completedProgress, Is.Not.Null);
                Assert.That(completedProgress!.BytesCompleted, Is.EqualTo(local.SizeBytes));
                Assert.That(completedProgress.BytesTotal, Is.EqualTo(local.SizeBytes));
                Assert.That(transferProgress.Values.Select(item => item.IsCompleted), Is.EqualTo(new[] { false, true }));
                Assert.That(activityProgress.Values.Select(item => item.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(fileStartedIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(transferStartedIndex, Is.GreaterThan(fileStartedIndex));
                Assert.That(transferCompletedIndex, Is.GreaterThan(transferStartedIndex));
                Assert.That(activityIndex, Is.GreaterThan(transferCompletedIndex));
                Assert.That(runCompletedIndex, Is.GreaterThan(activityIndex));
            });
        }

        [Test]
        public async Task RunOnceAsync_KeepsPlannedByteProgressStableWhenLazyHashCreatesConflict()
        {
            const string relativePath = "Docs/conflict.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-content");
            var local = new LocalFileSnapshot
            {
                RelativePath = relativePath,
                FullPath = Path.Combine(_root, "Docs", "conflict.txt"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
            };
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var scanner = new MetadataOnlyLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            var runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(scanner, RemoteTree(remote), remoteFiles, out _);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { RunProgress = runProgress });

            IReadOnlyList<SyncRunProgress> fileProgress = runProgress.Values
                .Where(item => item.Stage is SyncRunProgressStage.ReconcilingFiles or SyncRunProgressStage.Completed)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.EqualTo(1));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(fileProgress, Is.Not.Empty);
                Assert.That(fileProgress.Any(item => item.BytesTotal.HasValue), Is.True);
                Assert.That(
                    fileProgress.Where(item => item.BytesTotal.HasValue).All(item => item.BytesCompleted <= item.BytesTotal),
                    Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_ReportsLocalHashProgressWhenCheckingBaselineFile()
        {
            const string relativePath = "Docs/changed.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            local.ContentHash = string.Empty;
            local.LastWriteUtc = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc);
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            var scanner = new MetadataOnlyLocalFileScanner(local);
            var transferProgress = new RecordingProgress<SyncTransferProgress>();
            SyncEngine engine = CreateEngine(scanner, RemoteTree(remote), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText("old"), remote);

            await engine.RunOnceAsync(
                Pair(),
                new SyncRunOptions { TransferProgress = transferProgress });

            IReadOnlyList<SyncTransferProgress> hashProgress = transferProgress.Values
                .Where(static item => item.Direction == SyncTransferDirection.Hash)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.EqualTo(1));
                Assert.That(hashProgress, Has.Count.EqualTo(2));
                Assert.That(hashProgress[0].TransferredBytes, Is.Zero);
                Assert.That(hashProgress[0].TotalBytes, Is.EqualTo(local.SizeBytes));
                Assert.That(hashProgress[^1].TransferredBytes, Is.EqualTo(local.SizeBytes));
                Assert.That(hashProgress[^1].IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadsEditedHydratedFile()
        {
            const string relativePath = "Docs/hydrated-edited.txt";
            string oldHash = HashText("old-content");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new-content");
            local.LastWriteUtc = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc);
            NodeFileManifestDto remote = RemoteFile(relativePath, oldHash, sizeBytes: Encoding.UTF8.GetByteCount("old-content"));
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
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

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile?.Id, Is.EqualTo(remote.Id));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesPreservesConflictForEditedHydratedFile()
        {
            const string relativePath = "Docs/hydrated-conflict.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new-content");
            string oldHash = HashText("old-content");
            WriteFile(relativePath, "local-new-content");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new-content");
            local.LastWriteUtc = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc);
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, oldHash, remoteFileId, sizeBytes: Encoding.UTF8.GetByteCount("old-content"));
            NodeFileManifestDto changedRemote = RemoteFile(relativePath, Hash(remoteContent), remoteFileId, sizeBytes: remoteContent.Length);
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[changedRemote.Id] = remoteContent;
            FakeRemoteFilePlaceholderWriter materializationObserver = new();
            SyncEngine engine = CreateEngine(
                scanner,
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: materializationObserver);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = oldHash,
                LocalLastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
                LocalSizeBytes = Encoding.UTF8.GetByteCount("old-content"),
                RemoteNodeId = baselineRemote.NodeId,
                RemoteFileId = baselineRemote.Id,
                RemoteSizeBytes = baselineRemote.SizeBytes,
                RemoteContentHash = baselineRemote.ContentHash,
                RemoteETag = baselineRemote.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.EqualTo(new[] { changedRemote.Id }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(File.ReadAllText(Path.Combine(_root, "Docs", "hydrated-conflict.txt")), Is.EqualTo("local-new-content"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-new-content"));
                Assert.That(materializationObserver.FileMaterializationRequests, Has.Count.EqualTo(1));
                Assert.That(
                    materializationObserver.FileMaterializationRequests[0].RelativePath,
                    Does.Contain("Cotton conflict"));
                Assert.That(
                    materializationObserver.FileMaterializationRequests[0].RemoteFile.Id,
                    Is.EqualTo(changedRemote.Id));
                Assert.That(materializationObserver.FileExistsWhenMaterializationRequested, Is.EqualTo(new[] { false }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesTreatsNearSimultaneousHydratedEditsAsConflict()
        {
            const string relativePath = "Docs/hydrated-near-simultaneous.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-edit");
            string oldHash = HashText("old-content");
            WriteFile(relativePath, "local-edit");
            LocalFileSnapshot local = LocalFile(relativePath, "local-edit");
            local.LastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 3, DateTimeKind.Utc);
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, oldHash, remoteFileId, sizeBytes: Encoding.UTF8.GetByteCount("old-content"));
            NodeFileManifestDto changedRemote = RemoteFile(relativePath, Hash(remoteContent), remoteFileId, sizeBytes: remoteContent.Length);
            changedRemote.UpdatedAt = new DateTime(2026, 6, 2, 13, 0, 4, DateTimeKind.Utc);
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[changedRemote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(scanner, RemoteTree(changedRemote), remoteFiles, out SqliteSyncStateStore stateStore);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = oldHash,
                LocalLastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
                LocalSizeBytes = Encoding.UTF8.GetByteCount("old-content"),
                RemoteNodeId = baselineRemote.NodeId,
                RemoteFileId = baselineRemote.Id,
                RemoteSizeBytes = baselineRemote.SizeBytes,
                RemoteContentHash = baselineRemote.ContentHash,
                RemoteETag = baselineRemote.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 0, 1, DateTimeKind.Utc),
            });

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.EqualTo(new[] { changedRemote.Id }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(File.ReadAllText(Path.Combine(_root, "Docs", "hydrated-near-simultaneous.txt")), Is.EqualTo("local-edit"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-edit"));
            });
        }

        [Test]
        public async Task RunOnceAsync_DownloadsRemoteOnlyFileAndStoresBaseline()
        {
            byte[] content = Encoding.UTF8.GetBytes("remote-content");
            NodeFileManifestDto remote = RemoteFile("remote.txt", Hash(content), sizeBytes: content.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = content;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote.txt");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, "remote.txt")), Is.EqualTo("remote-content"));
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesCreatesRemoteOnlyPlaceholderWithoutDownloadingContent()
        {
            NodeFileManifestDto remote = RemoteFile("remote-only.txt", HashText("remote-content"), sizeBytes: long.MaxValue);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var runProgress = new RecordingProgress<SyncRunProgress>();
            var transferProgress = new RecordingProgress<SyncTransferProgress>();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    RunProgress = runProgress,
                    TransferProgress = transferProgress,
                });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-only.txt");
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, "remote-only.txt")), Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].LocalRootPath, Is.EqualTo(_root));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.Id, Is.EqualTo(remote.Id));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(transferProgress.Values, Is.Empty);
                Assert.That(runProgress.Values.Any(progress =>
                    progress.Stage == SyncRunProgressStage.CreatingPlaceholders
                    && progress.CurrentPath == "remote-only.txt"), Is.True);
                Assert.That(runProgress.Values.Last(progress => progress.IsCompleted).BytesTotal, Is.Zero);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.Null);
                Assert.That(entry.LocalSizeBytes, Is.Null);
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(remote.SizeBytes));
                Assert.That(entry.RemoteETag, Is.EqualTo(remote.ETag));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStartsPlaceholderCreationBeforeRemoteStreamingCompletes()
        {
            List<RemoteFileSnapshot> remoteFiles =
            [
                new() { RelativePath = "Desktop/first.txt", File = RemoteFile("Desktop/first.txt", HashText("first"), sizeBytes: 11) },
                new() { RelativePath = "Desktop/second.txt", File = RemoteFile("Desktop/second.txt", HashText("second"), sizeBytes: 12) },
                new() { RelativePath = "Desktop/third.txt", File = RemoteFile("Desktop/third.txt", HashText("third"), sizeBytes: 13) },
            ];
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var logger = new RecordingLogger<SyncEngine>();
            var engine = new SyncEngine(
                new FakeLocalFileScanner(),
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter,
                logger: logger);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            string startLog = logger.Entries
                .Single(entry => entry.Message.Contains(
                    "Starting initial streaming Windows virtual-files population",
                    StringComparison.Ordinal))
                .Message;
            string completionLog = logger.Entries
                .Single(entry => entry.Message.Contains(
                    "Completed initial streaming Windows virtual-files population",
                    StringComparison.Ordinal))
                .Message;
            string syncCompletionLog = logger.Entries
                .Single(entry => entry.Message.Contains(
                    "Completed sync pass for pair pair-a with Windows virtual-files placeholder work",
                    StringComparison.Ordinal))
                .Message;
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.FirstPlaceholderStartedBeforeStreamingCompleted, Is.True);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EquivalentTo(new[]
                {
                    "Desktop/first.txt",
                    "Desktop/second.txt",
                    "Desktop/third.txt",
                }));
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state, Has.Count.EqualTo(3));
                Assert.That(state.Select(entry => entry.PlaceholderHydrationState), Is.All.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(startLog, Does.Contain("queue capacity 1"));
                Assert.That(startLog, Does.Contain("placeholder concurrency"));
                Assert.That(startLog, Does.Contain("placeholder batch size"));
                Assert.That(startLog, Does.Contain("state batch size"));
                Assert.That(startLog, Does.Contain("managed heap"));
                Assert.That(completionLog, Does.Contain("3 files discovered"));
                Assert.That(completionLog, Does.Contain("dirs/sec"));
                Assert.That(completionLog, Does.Contain("files/sec"));
                Assert.That(completionLog, Does.Contain("remote pages read=1"));
                Assert.That(completionLog, Does.Contain("remote page latency total="));
                Assert.That(completionLog, Does.Contain("avg="));
                Assert.That(completionLog, Does.Contain("max="));
                Assert.That(completionLog, Does.Contain("last="));
                Assert.That(completionLog, Does.Contain("3 placeholders created or refreshed"));
                Assert.That(completionLog, Does.Contain("placeholders/sec"));
                Assert.That(completionLog, Does.Contain("state writes 3 file rows"));
                Assert.That(completionLog, Does.Contain("file write batches 1"));
                Assert.That(completionLog, Does.Contain("directory rows 0"));
                Assert.That(completionLog, Does.Contain("state write rate="));
                Assert.That(completionLog, Does.Contain("rows/sec"));
                Assert.That(completionLog, Does.Contain("managed heap start="));
                Assert.That(completionLog, Does.Contain("completed="));
                Assert.That(completionLog, Does.Contain("peak="));
                Assert.That(completionLog, Does.Contain("delta="));
                Assert.That(completionLog, Does.Contain("queue capacity=1"));
                Assert.That(completionLog, Does.Contain("placeholder concurrency="));
                Assert.That(completionLog, Does.Contain("placeholder batch size="));
                Assert.That(completionLog, Does.Contain("state batch size="));
                Assert.That(completionLog, Does.Contain("activities retained 0/0"));
                Assert.That(completionLog, Does.Contain("truncated=False"));
                Assert.That(syncCompletionLog, Does.Contain("0 activities"));
                Assert.That(syncCompletionLog, Does.Contain("0 file content transfers"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUsesBatchPlaceholderWriterDuringStreamingPopulation()
        {
            List<RemoteFileSnapshot> remoteFiles = Enumerable
                .Range(0, 7)
                .Select(index =>
                {
                    string relativePath = "Desktop/file-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".txt";
                    return new RemoteFileSnapshot
                    {
                        RelativePath = relativePath,
                        File = RemoteFile(relativePath, HashText("remote-" + index.ToString(CultureInfo.InvariantCulture)), sizeBytes: 10 + index),
                    };
                })
                .ToList();
            var remoteCrawler = new StreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new BatchRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(
                new FakeLocalFileScanner(),
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    InitialVirtualFilesPopulationQueueCapacity = 2,
                    InitialVirtualFilesPlaceholderBatchSize = 3,
                    InitialVirtualFilesPlaceholderConcurrency = 1,
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.SingleRequests, Is.Empty);
                Assert.That(
                    placeholderWriter.Batches.Select(batch => batch.ToArray()).ToArray(),
                    Is.EqualTo(new[]
                    {
                        new[] { "Desktop/file-0000.txt", "Desktop/file-0001.txt", "Desktop/file-0002.txt" },
                        new[] { "Desktop/file-0003.txt", "Desktop/file-0004.txt", "Desktop/file-0005.txt" },
                        new[] { "Desktop/file-0006.txt" },
                    }));
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state.Count(entry => entry.Kind == SyncEntryKind.File), Is.EqualTo(remoteFiles.Count));
                Assert.That(state.Where(entry => entry.Kind == SyncEntryKind.File), Has.All.Matches<SyncStateEntry>(
                    entry => entry.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                        && entry.PlaceholderIdentity is { Length: > 0 }
                        && entry.LocalContentHash is null
                        && entry.LocalSizeBytes is null));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesResumesStreamingPopulationWhenOnlyTrackedPlaceholdersExist()
        {
            NodeFileManifestDto existingRemote = RemoteFile(
                "Desktop/existing.txt",
                HashText("existing"),
                sizeBytes: 11);
            NodeFileManifestDto newRemote = RemoteFile(
                "Desktop/new.txt",
                HashText("new"),
                sizeBytes: 12);
            List<RemoteFileSnapshot> remoteFiles =
            [
                new() { RelativePath = "Desktop/existing.txt", File = existingRemote },
                new() { RelativePath = "Desktop/new.txt", File = newRemote },
            ];
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, "Desktop/existing.txt", existingRemote);
            var scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal("Desktop/existing.txt", existingRemote.SizeBytes));
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { "Desktop/new.txt" }));
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state, Has.Count.EqualTo(2));
                Assert.That(state.Select(entry => entry.PlaceholderHydrationState), Is.All.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesResumeLoadsStateWithoutScanningLocalPlaceholderTree()
        {
            NodeFileManifestDto newRemote = RemoteFile(
                "Desktop/new.txt",
                HashText("new"),
                sizeBytes: 12);
            NodeFileManifestDto existingRemote = RemoteFile(
                "Desktop/existing.txt",
                HashText("existing"),
                sizeBytes: 11);
            List<RemoteFileSnapshot> remoteFiles =
            [
                new() { RelativePath = "Desktop/new.txt", File = newRemote },
                new() { RelativePath = "Desktop/existing.txt", File = existingRemote },
            ];
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var innerStateStore = new SqliteSyncStateStore(_databasePath);
            await InsertPlaceholderBaselineAsync(innerStateStore, "Desktop/existing.txt", existingRemote);
            var stateStore = new StreamingOnlyStateStore(innerStateStore);
            var scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal("Desktop/existing.txt", existingRemote.SizeBytes));
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(stateStore.LoadPairEntriesCallCount, Is.EqualTo(1));
                Assert.That(stateStore.LoadEntriesByPathKeysCallCount, Is.Zero);
                Assert.That(stateStore.GetAsyncCallCount, Is.Zero);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { "Desktop/new.txt" }));
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamingRefreshesTrackedPlaceholderWhenRemoteChanged()
        {
            string relativePath = "Desktop/existing.txt";
            NodeFileManifestDto oldRemote = RemoteFile(
                relativePath,
                HashText("old"),
                sizeBytes: 11);
            NodeFileManifestDto newRemote = RemoteFile(
                relativePath,
                HashText("new"),
                id: oldRemote.Id,
                sizeBytes: 12);
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = newRemote }]);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, oldRemote);
            var scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal(relativePath, oldRemote.SizeBytes));
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state, Is.Not.Null);
                Assert.That(state!.RemoteContentHash, Is.EqualTo(newRemote.ContentHash));
                Assert.That(state.RemoteSizeBytes, Is.EqualTo(newRemote.SizeBytes));
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamingRefreshesHydratedPlaceholderBaseline()
        {
            string relativePath = "Desktop/available-offline.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
            NodeFileManifestDto oldRemote = RemoteFile(
                relativePath,
                local.ContentHash,
                sizeBytes: local.SizeBytes);
            local.LastWriteUtc = oldRemote.UpdatedAt;
            NodeFileManifestDto newRemote = RemoteFile(
                relativePath,
                HashText("new remote content"),
                id: oldRemote.Id,
                sizeBytes: 18);
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = newRemote }]);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new(
                remoteCrawler.FirstPlaceholderStarted,
                SyncPlaceholderHydrationState.Hydrated);
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(
                stateStore,
                relativePath,
                oldRemote,
                SyncPlaceholderHydrationState.Hydrated);
            SyncStateEntry existingState = (await stateStore.GetAsync("pair-a", relativePath))!;
            existingState.LocalContentHash = local.ContentHash;
            existingState.LocalSizeBytes = local.SizeBytes;
            existingState.LocalLastWriteUtc = local.LastWriteUtc;
            await stateStore.UpsertAsync(existingState);
            FakeLocalFileScanner scanner = new(local);
            SyncEngine engine = new(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state, Is.Not.Null);
                Assert.That(state!.LocalContentHash, Is.EqualTo(newRemote.ContentHash));
                Assert.That(state.LocalSizeBytes, Is.EqualTo(newRemote.SizeBytes));
                Assert.That(state.LocalLastWriteUtc, Is.EqualTo(newRemote.UpdatedAt));
                Assert.That(state.RemoteContentHash, Is.EqualTo(newRemote.ContentHash));
                Assert.That(state.RemoteSizeBytes, Is.EqualTo(newRemote.SizeBytes));
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(state.PlaceholderIdentity, Is.Not.Null.And.Not.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamingRemovesTrackedPlaceholderWhenRemoteDeleted()
        {
            string relativePath = "Desktop/deleted-online-only.txt";
            NodeFileManifestDto oldRemote = RemoteFile(
                relativePath,
                HashText("old"),
                sizeBytes: 11);
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new(_remoteRootNodeId, []);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, oldRemote);
            WriteFile(relativePath, "local placeholder");
            FakeLocalFileScanner scanner = new(CloudFilesPlaceholderLocal(relativePath, oldRemote.SizeBytes));
            SyncEngine engine = new(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", relativePath);
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(scanner.PathLookupCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(remoteFileSynchronizer.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
                Assert.That(state, Is.Null);
                Assert.That(File.Exists(fullPath), Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithInitialWindowsVirtualFilesFallsBackToReconcileWhenLocalFilesExist()
        {
            LocalFileSnapshot local = LocalFile("local.txt", "local-content");
            NodeFileManifestDto remote = RemoteFile("remote-only.txt", HashText("remote-content"), sizeBytes: 1024);
            var scanner = new FakeLocalFileScanner(local);
            RemoteTreeSnapshot remoteTree = RemoteTree(remote);
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = "remote-only.txt", File = remote }],
                snapshotCrawlResult: remoteTree);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            SyncStateEntry? localEntry = await stateStore.GetAsync("pair-a", "local.txt");
            SyncStateEntry? remoteEntry = await stateStore.GetAsync("pair-a", "remote-only.txt");
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteFileSynchronizer.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFileSynchronizer.Uploads[0].RelativePath, Is.EqualTo("local.txt"));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { "remote-only.txt" }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Uploaded,
                    SyncActivityKind.PlaceholderCreated,
                }));
                Assert.That(localEntry, Is.Not.Null);
                Assert.That(localEntry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(localEntry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(remoteEntry, Is.Not.Null);
                Assert.That(remoteEntry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(remoteEntry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamingDisabledUsesFullReconcile()
        {
            NodeFileManifestDto remote = RemoteFile("remote-only.txt", HashText("remote-content"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new();
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = "remote-only.txt", File = remote }],
                snapshotCrawlResult: RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { AllowInitialVirtualFilesStreaming = false });

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", "remote-only.txt");
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { "remote-only.txt" }));
                Assert.That(state, Is.Not.Null);
                Assert.That(state!.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithInitialWindowsVirtualFilesCreatesRemoteFoldersForPreservedDirectoryTree()
        {
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            var remoteTree = EmptyRemoteTree();
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [],
                snapshotCrawlResult: remoteTree);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteFileSynchronizer.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(placeholderWriter.DirectoryRequests, Is.Empty);
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { "Projects", "Archive" }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(state.Select(entry => entry.Kind), Is.All.EqualTo(SyncEntryKind.Directory));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Uploaded,
                    SyncActivityKind.Uploaded,
                }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesLogsStateFirstFallbackWithoutRelativePath()
        {
            string privateRelativePath = "Private/Family/video_2026_06_17.mp4";
            var scanner = new FakeLocalFileScanner();
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [],
                snapshotCrawlResult: EmptyRemoteTree());
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = privateRelativePath,
                Kind = SyncEntryKind.File,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                PlaceholderIdentity = [1, 2, 3],
            });
            var logger = new RecordingLogger<SyncEngine>();
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter,
                logger: logger);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            (LogLevel level, string message) = logger.Entries.Single(entry =>
                entry.Message.Contains("Skipping Windows virtual-files state-first resume plan", StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(level, Is.EqualTo(LogLevel.Information));
                Assert.That(message, Does.Contain("file state is missing a remote baseline"));
                Assert.That(message, Does.Contain("Entries seen=1"));
                Assert.That(message, Does.Contain("files=1"));
                Assert.That(message, Does.Not.Contain(privateRelativePath));
                Assert.That(message, Does.Not.Contain("video_2026_06_17"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesFallsBackWhenResumeStateHasMaterializedFile()
        {
            string relativePath = "hydrated.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "remote");
            NodeFileManifestDto previousRemote = RemoteFile(relativePath, local.ContentHash, sizeBytes: local.SizeBytes);
            FakeLocalFileScanner scanner = new(local);
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new(
                _remoteRootNodeId,
                [],
                snapshotCrawlResult: RemoteTree(previousRemote));
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(
                stateStore,
                relativePath,
                previousRemote,
                SyncPlaceholderHydrationState.Hydrated);
            SyncEngine engine = new(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> entries = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.EqualTo(1));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(entries, Has.Count.EqualTo(1));
                Assert.That(entries[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(entries[0].LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entries[0].RemoteContentHash, Is.EqualTo(previousRemote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesDoesNotResumeStreamingWhenTrackedPlaceholderIsMissing()
        {
            string relativePath = "Desktop/missing-online-only.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new();
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }],
                snapshotCrawlResult: RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);
            SyncEngine engine = new(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.Zero);
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(remoteFileSynchronizer.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.ActionRequiredMessage, Does.Contain("online-only file"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesCurrentUntrackedCloudFilesPlaceholderIdentity()
        {
            string relativePath = "Desktop/orphaned-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 12);
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }]);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt;
            var scanner = new FakeLocalFileScanner(local);
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state, Has.Count.EqualTo(1));
                Assert.That(state[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(state[0].RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(state[0].RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(state[0].RemoteSizeBytes, Is.EqualTo(remote.SizeBytes));
                Assert.That(state[0].PlaceholderIdentity, Is.Not.Null.And.Not.Empty);
                Assert.That(state[0].PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesUntrackedCloudFilesPlaceholderWhenRemoteMetadataDiffers()
        {
            string relativePath = "Desktop/orphaned-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 12);
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }]);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt.AddMinutes(-5);
            var scanner = new FakeLocalFileScanner(local);
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state, Has.Count.EqualTo(1));
                Assert.That(state[0].PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(state[0].PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamsWhenUntrackedDirectoriesAndPlaceholdersRemain()
        {
            string relativePath = "Desktop/orphaned-placeholder.txt";
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Desktop");
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 12);
            var remoteCrawler = new StreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }],
                [remoteDirectory]);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes));
            scanner.Directories.Add(LocalDirectory("Desktop"));
            var engine = new SyncEngine(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(remoteFileSynchronizer.Uploads, Is.Empty);
                Assert.That(placeholderWriter.DirectoryRequests.Select(request => request.RelativePath), Is.EqualTo(new[] { "Desktop" }));
                Assert.That(
                    placeholderWriter.CompletedDirectoryTreeRequests.Single().Select(request => request.RelativePath),
                    Is.EqualTo(new[] { "Desktop" }));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.TotalActivityCount, Is.Zero);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EquivalentTo(new[] { "Desktop", relativePath }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamingPublishesDiscoveryAsPlaceholderProgress()
        {
            List<RemoteFileSnapshot> remoteFiles =
            [
                new() { RelativePath = "Desktop/first.txt", File = RemoteFile("Desktop/first.txt", HashText("first"), sizeBytes: 11) },
                new() { RelativePath = "Desktop/second.txt", File = RemoteFile("Desktop/second.txt", HashText("second"), sizeBytes: 12) },
            ];
            var remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                remoteFiles,
                entriesExpected: remoteFiles.Count);
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var runProgress = new RecordingProgress<SyncRunProgress>();
            var engine = new SyncEngine(
                new FakeLocalFileScanner(),
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    InitialVirtualFilesPopulationQueueCapacity = 1,
                    RunProgress = runProgress,
                });

            List<SyncRunProgress> placeholderProgress = runProgress.Values
                .Where(progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(runProgress.Values.Any(progress => progress.Stage == SyncRunProgressStage.ScanningRemote), Is.False);
                Assert.That(runProgress.Values.Any(progress => progress.Stage == SyncRunProgressStage.ScanningLocal), Is.False);
                Assert.That(placeholderProgress, Is.Not.Empty);
                Assert.That(placeholderProgress.Any(progress =>
                    progress.FilesTotal == remoteFiles.Count
                    && progress.CurrentPath == "Desktop/first.txt"), Is.True);
                Assert.That(placeholderProgress.Any(progress => progress.FilesTotal == 1), Is.False);
                Assert.That(placeholderProgress.Any(progress =>
                    progress.FilesTotal == remoteFiles.Count), Is.True);
                Assert.That(placeholderProgress.Last().FilesTotal, Is.EqualTo(remoteFiles.Count));
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesCreatesLocalFolderForNestedRemoteOnlyPlaceholder()
        {
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            NodeFileManifestDto remote = RemoteFile("Projects/remote-only.txt", HashText("remote-content"), sizeBytes: long.MaxValue);
            RemoteTreeSnapshot remoteTree = RemoteTree(remote);
            remoteTree.Directories.Add(remoteDirectory);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            SyncStateEntry directoryEntry = state.Single(entry => entry.Kind == SyncEntryKind.Directory);
            SyncStateEntry fileEntry = state.Single(entry => entry.Kind == SyncEntryKind.File);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "Projects", "remote-only.txt")), Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.DirectoryRequests.Select(request => request.RelativePath), Is.EqualTo(new[] { "Projects" }));
                Assert.That(placeholderWriter.CompletedDirectoryRequests.Select(request => request.RelativePath), Is.EqualTo(new[] { "Projects" }));
                Assert.That(placeholderWriter.DirectoryExistsWhenCompleted, Is.EqualTo(new[] { true }));
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo("Projects/remote-only.txt"));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Downloaded,
                    SyncActivityKind.PlaceholderCreated,
                }));
                Assert.That(directoryEntry.RelativePath, Is.EqualTo("Projects"));
                Assert.That(directoryEntry.RemoteNodeId, Is.EqualTo(remoteDirectory.Node.Id));
                Assert.That(fileEntry.RelativePath, Is.EqualTo("Projects/remote-only.txt"));
                Assert.That(fileEntry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(fileEntry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesFinalizesDirectoryTreeAfterPlaceholderPopulation()
        {
            RemoteDirectorySnapshot parentDirectory = RemoteDirectory("Temp");
            RemoteDirectorySnapshot nestedDirectory = RemoteDirectory("Temp/Images");
            NodeFileManifestDto firstRemote = RemoteFile(
                "Temp/Images/photo.heic",
                HashText("photo"),
                sizeBytes: 1024);
            NodeFileManifestDto secondRemote = RemoteFile(
                "Temp/video.mp4",
                HashText("video"),
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(firstRemote, secondRemote);
            remoteTree.Directories.Add(parentDirectory);
            remoteTree.Directories.Add(nestedDirectory);
            var remoteCrawler = new StreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                remoteTree.Files,
                remoteTree.Directories);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            var runProgress = new RecordingProgress<SyncRunProgress>();
            var engine = new SyncEngine(
                new FakeLocalFileScanner(),
                remoteCrawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { RunProgress = runProgress });

            IReadOnlyList<RemoteDirectoryMaterializationRequest> completedTree =
                placeholderWriter.CompletedDirectoryTreeRequests.Single();
            List<SyncRunProgress> finalizingProgress = runProgress.Values
                .Where(progress => progress.Stage == SyncRunProgressStage.FinalizingCloudFiles)
                .ToList();
            List<SyncRunProgress> placeholderProgress = runProgress.Values
                .Where(progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(
                    placeholderWriter.CompletedDirectoryRequests.Select(static request => request.RelativePath),
                    Is.EqualTo(new[] { "Temp", "Temp/Images" }));
                Assert.That(
                    completedTree.Select(static request => request.RelativePath),
                    Is.EquivalentTo(new[] { "Temp", "Temp/Images" }));
                Assert.That(
                    placeholderWriter.PlaceholderCountWhenDirectoryTreeCompleted,
                    Is.EqualTo(new[] { 2 }));
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteCrawler.SnapshotCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderProgress, Is.Not.Empty);
                Assert.That(placeholderProgress.Last().FilesCompleted, Is.EqualTo(4));
                Assert.That(placeholderProgress.Last().FilesTotal, Is.EqualTo(4));
                Assert.That(placeholderProgress.Any(progress =>
                    progress.FilesTotal == 3
                    && progress.CurrentPath == "Temp/Images/photo.heic"), Is.True);
                Assert.That(
                    finalizingProgress.Select(static progress => new
                    {
                        progress.FilesCompleted,
                        progress.FilesTotal,
                        progress.IsCompleted,
                    }),
                    Is.EqualTo(new[]
                    {
                        new { FilesCompleted = 0, FilesTotal = (int?)2, IsCompleted = false },
                        new { FilesCompleted = 2, FilesTotal = (int?)2, IsCompleted = true },
                    }));
                Assert.That(
                    runProgress.Values.FindIndex(progress => progress.Stage == SyncRunProgressStage.FinalizingCloudFiles),
                    Is.LessThan(runProgress.Values.FindIndex(progress => progress.Stage == SyncRunProgressStage.Completed)));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesPopulatesLargeRemoteTreeIncrementally()
        {
            const int fileCount = 1_000;
            NodeFileManifestDto[] remoteFiles = Enumerable
                .Range(0, fileCount)
                .Select(index => RemoteFile(
                    "LargeTree/file-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".txt",
                    HashText("remote-content-" + index.ToString(CultureInfo.InvariantCulture)),
                    sizeBytes: 1024 + index))
                .ToArray();
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFiles);
            remoteTree.Directories.Add(RemoteDirectory("LargeTree"));
            var remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            var cooperativeYieldRequestCounts = new List<int>();
            var runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                remoteFileSynchronizer,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    RunProgress = runProgress,
                    MaximumStoredResultActivities = fileCount + 2,
                    CooperativeYieldAsync = _ =>
                    {
                        cooperativeYieldRequestCounts.Add(placeholderWriter.Requests.Count);
                        return ValueTask.CompletedTask;
                    },
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            List<SyncRunProgress> placeholderProgress = runProgress.Values
                .Where(progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(remoteFileSynchronizer.DownloadCalls, Is.Empty);
                Assert.That(remoteFileSynchronizer.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(fileCount));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Does.Contain("LargeTree/file-0000.txt"));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Does.Contain("LargeTree/file-0999.txt"));
                Assert.That(result.Activities.Count(activity => activity.Kind == SyncActivityKind.PlaceholderCreated), Is.EqualTo(fileCount));
                Assert.That(result.Activities.Count(activity => activity.Kind == SyncActivityKind.Downloaded), Is.EqualTo(1));
                Assert.That(placeholderProgress, Has.Count.GreaterThanOrEqualTo(5));
                Assert.That(placeholderProgress.Any(progress => progress.FilesCompleted > 0 && progress.FilesCompleted < fileCount), Is.True);
                Assert.That(placeholderProgress.Last().FilesTotal, Is.EqualTo(fileCount));
                Assert.That(cooperativeYieldRequestCounts, Has.Count.GreaterThanOrEqualTo(5));
                Assert.That(cooperativeYieldRequestCounts[0], Is.EqualTo(25));
                Assert.That(cooperativeYieldRequestCounts, Has.All.LessThan(fileCount));
                Assert.That(state.Count(entry => entry.Kind == SyncEntryKind.File), Is.EqualTo(fileCount));
                Assert.That(state.Where(entry => entry.Kind == SyncEntryKind.File), Has.All.Matches<SyncStateEntry>(
                    entry => entry.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                        && entry.PlaceholderIdentity is { Length: > 0 }
                        && entry.LocalContentHash is null
                        && entry.LocalSizeBytes is null));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesDoesNotFallBackToDownloadWhenPlaceholderWriterIsMissing()
        {
            NodeFileManifestDto remote = RemoteFile("remote-only.txt", HashText("remote-content"), sizeBytes: 1024);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-only.txt");
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, "remote-only.txt")), Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(entry, Is.Null);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped }));
                Assert.That(result.ActionRequiredMessage, Does.Contain("placeholder writer"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesReportsPlaceholderUnavailableAsActionRequired()
        {
            NodeFileManifestDto remote = RemoteFile("remote-only.txt", HashText("remote-content"), sizeBytes: 1024);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter
            {
                UnavailableReason = "Cloud Files sync root is not connected.",
            };
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-only.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(entry, Is.Null);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped }));
                Assert.That(result.ActionRequiredMessage, Is.EqualTo("Cloud Files sync root is not connected."));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRestoresMissingRemoteOnlyPlaceholderDuringFullReconcile()
        {
            NodeFileManifestDto remote = RemoteFile("placeholder-deleted.txt", HashText("remote-content"), sizeBytes: 1024);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, "placeholder-deleted.txt", remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { RestoreMissingRemoteOnlyPlaceholders = true });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "placeholder-deleted.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath),
                    Is.EqualTo(new[] { "placeholder-deleted.txt" }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(x => x.Kind),
                    Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesKeepsExistingCloudFilesPlaceholderDuringUnrelatedLocalCreate()
        {
            const string existingPath = "local-upload.txt";
            NodeFileManifestDto existingRemote = RemoteFile(existingPath, HashText("remote-content"), sizeBytes: 79);
            LocalFileSnapshot existingPlaceholder = CloudFilesPlaceholderLocal(existingPath, existingRemote.SizeBytes);
            LocalFileSnapshot newLocal = LocalFile("remote-origin.txt", "Cotton Sync Desktop live smoke from client B");
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(existingPlaceholder, newLocal),
                RemoteTree(existingRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, existingPath, existingRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? placeholderEntry = await stateStore.GetAsync("pair-a", existingPath);
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.LocalFile.RelativePath), Is.EqualTo(new[] { newLocal.RelativePath }));
                Assert.That(placeholderEntry, Is.Not.Null);
                Assert.That(placeholderEntry!.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(placeholderEntry.RemoteContentHash, Is.EqualTo(existingRemote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesRemoteOnlyPlaceholderWhenRemoteChanges()
        {
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile("remote-updated.txt", HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto changedRemote = RemoteFile("remote-updated.txt", HashText("new-content"), remoteFileId, sizeBytes: 2048);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, "remote-updated.txt", baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-updated.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo("remote-updated.txt"));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.ContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(changedRemote.SizeBytes));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesExistingCloudFilesPlaceholderWhenRemoteChanges()
        {
            const string relativePath = "remote-updated.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto changedRemote = RemoteFile(relativePath, HashText("new-content"), remoteFileId, sizeBytes: 2048);
            LocalFileSnapshot localPlaceholder = CloudFilesPlaceholderLocal(relativePath, baselineRemote.SizeBytes);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(localPlaceholder),
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.ContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesAdoptsUnchangedHydratedPlaceholderWithoutUpload()
        {
            const string relativePath = "remote-hydrated.txt";
            const string content = "remote-content";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText(content), remoteFileId, sizeBytes: Encoding.UTF8.GetByteCount(content));
            LocalFileSnapshot local = LocalFile(relativePath, content);
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Converged }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.LocalSizeBytes, Is.EqualTo(local.SizeBytes));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(entry.PlaceholderIdentity, Is.Not.Null.And.Not.Empty);
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadsMaterializedCloudFileOverRemoteOnlyBaseline()
        {
            const string relativePath = "remote-updated.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto currentRemote = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            LocalFileSnapshot local = LocalFile(relativePath, "local replacement");
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                RemoteTree(currentRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile?.Id, Is.EqualTo(remoteFileId));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.None));
                Assert.That(entry.PlaceholderIdentity, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesDehydratedPlaceholderWhenRemoteChanges()
        {
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile("remote-updated.txt", HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto changedRemote = RemoteFile("remote-updated.txt", HashText("new-content"), remoteFileId, sizeBytes: 2048);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(
                stateStore,
                "remote-updated.txt",
                baselineRemote,
                SyncPlaceholderHydrationState.Dehydrated);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-updated.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo("remote-updated.txt"));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.ContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(changedRemote.SizeBytes));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRemovesLocalPlaceholderWhenRemoteIsDeleted()
        {
            const string relativePath = "remote-deleted-placeholder.txt";
            WriteFile(relativePath, string.Empty);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, 1024);
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                EmptyRemoteTree(),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string[] tombstones = Directory.GetFiles(
                Path.Combine(_root, ".cotton-sync", "deleted"),
                "*",
                SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(tombstones.Select(Path.GetFileName), Does.Contain(relativePath));
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesPreservesMaterializedLocalWhenRemotePlaceholderIsDeleted()
        {
            const string relativePath = "remote-deleted-materialized.txt";
            const string localContent = "local replacement";
            WriteFile(relativePath, localContent);
            LocalFileSnapshot local = LocalFile(relativePath, localContent);
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                EmptyRemoteTree(),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile, Is.Null);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(File.Exists(fullPath), Is.True);
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo(localContent));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.None));
                Assert.That(entry.PlaceholderIdentity, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMovesRemoteOnlyPlaceholderWhenRemotePathChanges()
        {
            const string oldPath = "Docs/old-name.txt";
            const string newPath = "Docs/new-name.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto movedRemote = RemoteFile(newPath, baselineRemote.ContentHash, remoteFileId, sizeBytes: 1024);
            WriteFile(oldPath, string.Empty);
            LocalFileSnapshot oldLocalPlaceholder = CloudFilesPlaceholderLocal(oldPath, baselineRemote.SizeBytes);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(oldLocalPlaceholder),
                RemoteTree(movedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EquivalentTo(new[]
                {
                    SyncActivityKind.DeletedLocal,
                    SyncActivityKind.PlaceholderCreated,
                }));
                Assert.That(File.Exists(Path.Combine(_root, oldPath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(File.Exists(Path.Combine(_root, newPath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Not.Null);
                Assert.That(newEntry!.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(newEntry.RemoteContentHash, Is.EqualTo(baselineRemote.ContentHash));
                Assert.That(newEntry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMovesOnlineOnlyPlaceholderDuringParentRename()
        {
            const string oldDirectoryPath = "Library";
            const string newDirectoryPath = "LibraryRenamed";
            const string oldPath = "Library/online-only.bin";
            const string newPath = "LibraryRenamed/online-only.bin";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(oldDirectoryPath);
            RemoteTreeSnapshot remoteTree = RemoteTree(remote);
            remoteTree.Directories.Add(remoteDirectory);
            LocalFileSnapshot movedLocalPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            movedLocalPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(movedLocalPlaceholder);
            scanner.Directories.Add(LocalDirectory(newDirectoryPath));
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertDirectoryBaselineAsync(stateStore, oldDirectoryPath, remoteDirectory.Node);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldDirectoryPath, newDirectoryPath]),
                });

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(remoteFiles.Moves[0].ExistingRemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(scanner.LastIncludeDirectoryDescendants, Is.False);
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    result.Activities.Count(activity =>
                        activity.Kind == SyncActivityKind.Moved
                        && string.Equals(activity.RelativePath, newPath, StringComparison.OrdinalIgnoreCase)),
                    Is.EqualTo(1));
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { newDirectoryPath }));
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (remoteDirectory.Node.Id, false) }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Not.Null);
                Assert.That(newEntry!.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(newEntry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(newEntry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(newEntry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRenamesOnlineOnlyPlaceholderWithinDirectory()
        {
            const string oldPath = "Online/rename.bin";
            const string newPath = "Online/renamed.bin";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            LocalFileSnapshot renamedLocalPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            renamedLocalPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(renamedLocalPlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(remoteFiles.Moves[0].ExistingRemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    result.Activities.Select(activity => activity.Kind),
                    Is.EqualTo(new[] { SyncActivityKind.Moved }));
                Assert.That(oldState, Is.Null);
                Assert.That(newState, Is.Not.Null);
                Assert.That(newState!.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(newState.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(newState.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotRenameExplicitlyDeletedPlaceholder()
        {
            const string oldPath = "Online/delete.bin";
            const string newPath = "Online/replacement.bin";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot replacementPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            replacementPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(replacementPlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath], [oldPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(oldState, Is.Null);
                Assert.That(newState, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotRenameChangedNameAcrossDirectories()
        {
            const string oldPath = "First/old.bin";
            const string newPath = "Second/new.bin";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot candidatePlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            candidatePlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(candidatePlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(oldState, Is.Not.Null);
                Assert.That(oldState!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(newState, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesMovesNestedDirectorySubtreeInOnePass()
        {
            const string oldDirectoryPath = "Source/Album";
            const string oldNestedDirectoryPath = "Source/Album/Disc1";
            const string newDirectoryPath = "Target/Album";
            const string newNestedDirectoryPath = "Target/Album/Disc1";
            const string oldFilePath = "Source/Album/Disc1/online-only.bin";
            const string newFilePath = "Target/Album/Disc1/online-only.bin";
            RemoteDirectorySnapshot sourceDirectory = RemoteDirectory("Source");
            RemoteDirectorySnapshot targetDirectory = RemoteDirectory("Target");
            RemoteDirectorySnapshot oldDirectory = RemoteDirectory(oldDirectoryPath, sourceDirectory.Node.Id);
            RemoteDirectorySnapshot oldNestedDirectory = RemoteDirectory(
                oldNestedDirectoryPath,
                oldDirectory.Node.Id);
            NodeFileManifestDto remoteFile = RemoteFile(
                oldFilePath,
                HashText("remote-content"),
                sizeBytes: 1024);
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.Add(sourceDirectory);
            remoteTree.Directories.Add(targetDirectory);
            remoteTree.Directories.Add(oldDirectory);
            remoteTree.Directories.Add(oldNestedDirectory);
            LocalFileSnapshot movedPlaceholder = CloudFilesPlaceholderLocal(newFilePath, remoteFile.SizeBytes);
            movedPlaceholder.LastWriteUtc = remoteFile.UpdatedAt;
            FakeLocalFileScanner scanner = new(movedPlaceholder);
            scanner.Directories.Add(LocalDirectory("Source"));
            scanner.Directories.Add(LocalDirectory("Target"));
            scanner.Directories.Add(LocalDirectory(newDirectoryPath));
            scanner.Directories.Add(LocalDirectory(newNestedDirectoryPath));
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertDirectoryBaselineAsync(stateStore, "Source", sourceDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Target", targetDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldDirectoryPath, oldDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldNestedDirectoryPath, oldNestedDirectory.Node);
            await InsertPlaceholderBaselineAsync(stateStore, oldFilePath, remoteFile);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths(
                    [
                        "Source",
                        oldDirectoryPath,
                        oldNestedDirectoryPath,
                        oldFilePath,
                        "Target",
                        newDirectoryPath,
                        newNestedDirectoryPath,
                        newFilePath,
                    ]),
                });

            SyncStateEntry? oldDirectoryState = await stateStore.GetAsync("pair-a", oldDirectoryPath);
            SyncStateEntry? oldNestedDirectoryState = await stateStore.GetAsync("pair-a", oldNestedDirectoryPath);
            SyncStateEntry? oldFileState = await stateStore.GetAsync("pair-a", oldFilePath);
            SyncStateEntry? newDirectoryState = await stateStore.GetAsync("pair-a", newDirectoryPath);
            SyncStateEntry? newNestedDirectoryState = await stateStore.GetAsync("pair-a", newNestedDirectoryPath);
            SyncStateEntry? newFileState = await stateStore.GetAsync("pair-a", newFilePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { "Album", "Disc1" }));
                Assert.That(
                    remoteDirectories.Deletes,
                    Is.EqualTo(new[]
                    {
                        (oldNestedDirectory.Node.Id, false),
                        (oldDirectory.Node.Id, false),
                    }));
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newFilePath));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Skipped), Is.False);
                Assert.That(oldDirectoryState, Is.Null);
                Assert.That(oldNestedDirectoryState, Is.Null);
                Assert.That(oldFileState, Is.Null);
                Assert.That(newDirectoryState?.RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(newNestedDirectoryState?.RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[1].ReturnedNode.Id));
                Assert.That(newFileState?.RemoteFileId, Is.EqualTo(remoteFile.Id));
                Assert.That(newFileState?.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotDeleteSubtreeWithUntrackedRemoteDescendant()
        {
            const string oldDirectoryPath = "Library";
            const string newDirectoryPath = "LibraryMoved";
            const string oldTrackedFilePath = "Library/tracked.bin";
            const string newTrackedFilePath = "LibraryMoved/tracked.bin";
            RemoteDirectorySnapshot oldDirectory = RemoteDirectory(oldDirectoryPath);
            NodeFileManifestDto trackedRemoteFile = RemoteFile(
                oldTrackedFilePath,
                HashText("tracked-content"),
                sizeBytes: 1024);
            NodeFileManifestDto untrackedRemoteFile = RemoteFile(
                "Library/untracked.bin",
                HashText("untracked-content"),
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(trackedRemoteFile, untrackedRemoteFile);
            remoteTree.Directories.Add(oldDirectory);
            LocalFileSnapshot movedPlaceholder = CloudFilesPlaceholderLocal(
                newTrackedFilePath,
                trackedRemoteFile.SizeBytes);
            movedPlaceholder.LastWriteUtc = trackedRemoteFile.UpdatedAt;
            FakeLocalFileScanner scanner = new(movedPlaceholder);
            scanner.Directories.Add(LocalDirectory(newDirectoryPath));
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());
            await InsertDirectoryBaselineAsync(stateStore, oldDirectoryPath, oldDirectory.Node);
            await InsertPlaceholderBaselineAsync(stateStore, oldTrackedFilePath, trackedRemoteFile);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldDirectoryPath, newDirectoryPath]),
                });

            SyncStateEntry? oldDirectoryState = await stateStore.GetAsync("pair-a", oldDirectoryPath);
            SyncStateEntry? oldTrackedFileState = await stateStore.GetAsync("pair-a", oldTrackedFilePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(oldDirectoryState, Is.Not.Null);
                Assert.That(oldTrackedFileState, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesDoesNotGuessAmbiguousOnlineOnlyPlaceholderMove()
        {
            const string oldPath = "Library/online-only.bin";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot firstCandidate = CloudFilesPlaceholderLocal("First/online-only.bin", remote.SizeBytes);
            firstCandidate.LastWriteUtc = remote.UpdatedAt;
            LocalFileSnapshot secondCandidate = CloudFilesPlaceholderLocal("Second/online-only.bin", remote.SizeBytes);
            secondCandidate.LastWriteUtc = remote.UpdatedAt;
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(firstCandidate, secondCandidate),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", firstCandidate.RelativePath);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", secondCandidate.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.ActionRequiredMessage, Does.Contain("deleted or moved locally"));
                Assert.That(oldEntry, Is.Not.Null);
                Assert.That(firstEntry, Is.Null);
                Assert.That(secondEntry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesDoesNotUploadRenamedRemoteOnlyPlaceholder()
        {
            NodeFileManifestDto remote = RemoteFile("placeholder-renamed.txt", HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot renamedLocal = LocalFile("aaa-renamed-placeholder.txt", "remote-content");
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(renamedLocal),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, "placeholder-renamed.txt", remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", "placeholder-renamed.txt");
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", "aaa-renamed-placeholder.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(result.ActionRequiredMessage, Does.Contain("deleted or moved locally"));
                Assert.That(oldEntry, Is.Not.Null);
                Assert.That(newEntry, Is.Null);
            });
        }

        [Test]
        public void RunOnceAsync_FailsBeforeDownloadWhenPlannedDownloadsExceedFreeSpace()
        {
            NodeFileManifestDto remote = RemoteFile("huge.bin", HashText("huge"), sizeBytes: long.MaxValue);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out _);

            LocalInsufficientDiskSpaceException? exception = Assert.ThrowsAsync<LocalInsufficientDiskSpaceException>(
                () => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("Not enough disk space"));
                Assert.That(exception?.Message, Does.Contain("huge.bin"));
                Assert.That(exception?.RelativePath, Is.EqualTo("huge.bin"));
                Assert.That(exception?.RequiredBytes, Is.EqualTo(long.MaxValue));
                Assert.That(File.Exists(Path.Combine(_root, "huge.bin")), Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_CreatesRemoteFolderForLocalOnlyEmptyDirectoryAndStoresBaseline()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects", "Archive"));
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates, Has.Count.EqualTo(2));
                Assert.That(remoteDirectories.Creates[0].ParentNodeId, Is.EqualTo(_remoteRootNodeId));
                Assert.That(remoteDirectories.Creates[0].Name, Is.EqualTo("Projects"));
                Assert.That(remoteDirectories.Creates[1].ParentNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(remoteDirectories.Creates[1].Name, Is.EqualTo("Archive"));
                Assert.That(state.Select(entry => entry.Kind), Is.EqualTo(new[] { SyncEntryKind.Directory, SyncEntryKind.Directory }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(state.Select(entry => entry.RemoteNodeId), Is.EqualTo(remoteDirectories.Creates.Select(call => call.ReturnedNode.Id)));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Uploaded }));
            });
        }

        [Test]
        public async Task RunOnceAsync_ReusesExistingRemoteFolderWhenLocalCreateHitsConflict()
        {
            var existingProjectsNode = new NodeDto
            {
                Id = Guid.NewGuid(),
                ParentId = _remoteRootNodeId,
                Name = "Projects",
            };
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            remoteDirectories.ConflictCreates.Add((_remoteRootNodeId, "Projects"));
            remoteDirectories.ExistingDirectories.Add(existingProjectsNode);
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.CreateAttempts.Select(call => call.Name), Is.EqualTo(new[] { "Projects", "Archive" }));
                Assert.That(remoteDirectories.FindChildDirectoryCalls, Is.EqualTo(new[] { (_remoteRootNodeId, "Projects") }));
                Assert.That(remoteDirectories.Creates, Has.Count.EqualTo(1));
                Assert.That(remoteDirectories.Creates[0].ParentNodeId, Is.EqualTo(existingProjectsNode.Id));
                Assert.That(remoteDirectories.Creates[0].Name, Is.EqualTo("Archive"));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(state.Select(entry => entry.RemoteNodeId), Is.EqualTo(new[] { existingProjectsNode.Id, remoteDirectories.Creates[0].ReturnedNode.Id }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Uploaded }));
                Assert.That(result.Activities[0].Details, Does.Contain("Reused existing remote folder"));
            });
        }

        [Test]
        public async Task RunOnceAsync_CreatesLocalFolderForRemoteOnlyEmptyDirectoryAndStoresBaseline()
        {
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(state, Has.Count.EqualTo(1));
                Assert.That(state[0].Kind, Is.EqualTo(SyncEntryKind.Directory));
                Assert.That(state[0].RelativePath, Is.EqualTo("Projects"));
                Assert.That(state[0].RemoteNodeId, Is.EqualTo(remoteDirectory.Node.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
            });
        }

        [Test]
        public async Task RunOnceAsync_DeletesRemoteEmptyDirectoryWhenBaselineKnowsLocalDelete()
        {
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (remoteDirectory.Node.Id, false) }));
                Assert.That(state, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
            });
        }

        [Test]
        public async Task RunOnceAsync_DeletesLocalEmptyDirectoryWhenBaselineKnowsRemoteDelete()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects"));
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                },
            };
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.False);
                Assert.That(state, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
            });
        }

        [Test]
        public async Task RunOnceAsync_SkipsLocalDirectoryDeleteWhenFolderIsNotEmpty()
        {
            WriteFile("Projects/keep.txt", "keep");
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            LocalFileSnapshot localFile = LocalFile("Projects/keep.txt", "keep");
            var scanner = new FakeLocalFileScanner(localFile)
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                },
            };
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", "Projects");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "Projects", "keep.txt")), Is.True);
                Assert.That(state, Is.Not.Null);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped, SyncActivityKind.Uploaded }));
                Assert.That(result.Activities[0].RequiresUserAction, Is.False);
                Assert.That(result.Activities[0].Details, Does.Contain("not empty"));
            });
        }

        [Test]
        public async Task RunOnceAsync_BlocksRemoteDirectoryDeletesOverRunLimit()
        {
            RemoteDirectorySnapshot first = RemoteDirectory("One");
            RemoteDirectorySnapshot second = RemoteDirectory("Two");
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(first);
            remoteTree.Directories.Add(second);
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, "One", first.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Two", second.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "One", "Two" }));
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped, SyncActivityKind.Skipped }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(result.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDeletesLocalDirectorySubtreeInOnePass()
        {
            const string rootPath = "Library";
            const string nestedPath = "Library/Disc1";
            const string deepPath = "Library/Disc1/Disc2";
            const string emptyPath = "Library/Empty";
            const string rootFilePath = "Library/root.bin";
            const string nestedFilePath = "Library/Disc1/Disc2/nested.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            RemoteDirectorySnapshot nested = RemoteDirectory(nestedPath, root.Node.Id);
            RemoteDirectorySnapshot deep = RemoteDirectory(deepPath, nested.Node.Id);
            RemoteDirectorySnapshot empty = RemoteDirectory(emptyPath, root.Node.Id);
            NodeFileManifestDto rootFile = RemoteFile(rootFilePath, HashText("root-content"), sizeBytes: 1024);
            NodeFileManifestDto nestedFile = RemoteFile(nestedFilePath, HashText("nested-content"), sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(rootFile, nestedFile);
            remoteTree.Directories.Add(root);
            remoteTree.Directories.Add(nested);
            remoteTree.Directories.Add(deep);
            remoteTree.Directories.Add(empty);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertDirectoryBaselineAsync(stateStore, nestedPath, nested.Node);
            await InsertDirectoryBaselineAsync(stateStore, deepPath, deep.Node);
            await InsertDirectoryBaselineAsync(stateStore, emptyPath, empty.Node);
            await InsertPlaceholderBaselineAsync(stateStore, rootFilePath, rootFile);
            await InsertPlaceholderBaselineAsync(stateStore, nestedFilePath, nestedFile);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(
                    remoteFiles.Deletes.Select(call => call.NodeFileId),
                    Is.EquivalentTo(new[] { rootFile.Id, nestedFile.Id }));
                Assert.That(
                    remoteDirectories.Deletes,
                    Is.EqualTo(new[]
                    {
                        (deep.Node.Id, false),
                        (nested.Node.Id, false),
                        (empty.Node.Id, false),
                        (root.Node.Id, false),
                    }));
                Assert.That(state, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Skipped), Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotCascadeDeletedSubtreeWithUntrackedRemoteFile()
        {
            const string rootPath = "Library";
            const string trackedPath = "Library/tracked.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            NodeFileManifestDto tracked = RemoteFile(trackedPath, HashText("tracked-content"), sizeBytes: 1024);
            NodeFileManifestDto untracked = RemoteFile(
                "Library/untracked.bin",
                HashText("untracked-content"),
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(tracked, untracked);
            remoteTree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertPlaceholderBaselineAsync(stateStore, trackedPath, tracked);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                });

            SyncStateEntry? rootState = await stateStore.GetAsync("pair-a", rootPath);
            SyncStateEntry? trackedState = await stateStore.GetAsync("pair-a", trackedPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(rootState, Is.Not.Null);
                Assert.That(trackedState, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotCascadeDeletedSubtreeWithChangedRemoteFile()
        {
            const string rootPath = "Library";
            const string filePath = "Library/tracked.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            NodeFileManifestDto baseline = RemoteFile(filePath, HashText("baseline-content"), sizeBytes: 1024);
            NodeFileManifestDto changed = RemoteFile(
                filePath,
                HashText("changed-content"),
                baseline.Id,
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(changed);
            remoteTree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertPlaceholderBaselineAsync(stateStore, filePath, baseline);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                });

            SyncStateEntry? rootState = await stateStore.GetAsync("pair-a", rootPath);
            SyncStateEntry? fileState = await stateStore.GetAsync("pair-a", filePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(rootState, Is.Not.Null);
                Assert.That(fileState, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesCountsDeletedSubtreeDirectoriesInRemoteDeleteGuard()
        {
            const string rootPath = "Library";
            const string filePath = "Library/tracked.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            NodeFileManifestDto remoteFile = RemoteFile(filePath, HashText("tracked-content"), sizeBytes: 1024);
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertPlaceholderBaselineAsync(stateStore, filePath, remoteFile);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                    MaximumRemoteDeletesPerRun = 1,
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(state, Has.Count.EqualTo(2));
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities, Has.Count.EqualTo(1));
                Assert.That(result.Activities[0].Kind, Is.EqualTo(SyncActivityKind.Skipped));
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotCascadeRemoteDirectoryDeletesInsideOneRun()
        {
            RemoteDirectorySnapshot parent = RemoteDirectory("Projects");
            RemoteDirectorySnapshot child = RemoteDirectory("Projects/Archive", parent.Node.Id);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(parent);
            remoteTree.Directories.Add(child);
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", parent.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Projects/Archive", child.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (child.Node.Id, false) }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects" }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote, SyncActivityKind.Skipped }));
                Assert.That(result.Activities[1].Details, Does.Contain("not empty"));
            });
        }

        [Test]
        public async Task RunOnceAsync_BlocksWholeRemoteDeletedDirectorySubtreeOverRunLimit()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects", "Archive"));
            RemoteDirectorySnapshot parent = RemoteDirectory("Projects");
            RemoteDirectorySnapshot child = RemoteDirectory("Projects/Archive", parent.Node.Id);
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", parent.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Projects/Archive", child.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumLocalDeletesPerRun = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects", "Archive")), Is.True);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped, SyncActivityKind.Skipped }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities.Select(activity => activity.Details), Is.All.Contains("2 pending deletes exceed limit 1"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesLocalFolderWhenRemoteFileInsideIsDeleted()
        {
            const string directoryPath = "Projects";
            const string filePath = "Projects/deleted-remotely.txt";
            WriteFile(filePath, "baseline-content");
            LocalFileSnapshot local = LocalFile(filePath, "baseline-content");
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(directoryPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            var scanner = new FakeLocalFileScanner(local)
            {
                Directories =
                {
                    LocalDirectory(directoryPath),
                },
            };
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, directoryPath, remoteDirectory.Node);
            await InsertBaselineAsync(
                stateStore,
                filePath,
                local.ContentHash,
                RemoteFile(filePath, local.ContentHash));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, directoryPath)), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "Projects", "deleted-remotely.txt")), Is.False);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { directoryPath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PropagatesLocalEmptyDirectoryRenameAsCreateAndDelete()
        {
            const string oldPath = "Projects";
            const string newPath = "ProjectsRenamed";
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(oldRemoteDirectory);
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(newPath),
                },
            };
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { newPath }));
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (oldRemoteDirectory.Node.Id, false) }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(state[0].RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.DeletedRemote }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMovesRemoteDirectorySubtreeByStableIds()
        {
            const string oldRootPath = "Projects";
            const string oldChildPath = "Projects/Source";
            const string oldFilePath = "Projects/Source/data.bin";
            const string targetParentPath = "Archive";
            const string newRootPath = "Archive/ProjectsRenamed";
            const string newChildPath = "Archive/ProjectsRenamed/Source";
            const string newFilePath = "Archive/ProjectsRenamed/Source/data.bin";
            const string content = "hydrated remote-move content";
            WriteFile(oldFilePath, content);
            Directory.CreateDirectory(Path.Combine(_root, targetParentPath));
            LocalFileSnapshot localFile = LocalFile(oldFilePath, content);
            string localContentHash = localFile.ContentHash;
            localFile.ContentHash = string.Empty;
            localFile.IsCloudFilesPlaceholder = true;
            RemoteDirectorySnapshot targetParent = RemoteDirectory(targetParentPath);
            RemoteDirectorySnapshot oldRoot = RemoteDirectory(oldRootPath);
            RemoteDirectorySnapshot oldChild = RemoteDirectory(oldChildPath, oldRoot.Node.Id);
            RemoteDirectorySnapshot movedRoot = new()
            {
                RelativePath = newRootPath,
                Node = new NodeDto
                {
                    Id = oldRoot.Node.Id,
                    ParentId = targetParent.Node.Id,
                    Name = "ProjectsRenamed",
                },
            };
            RemoteDirectorySnapshot movedChild = new()
            {
                RelativePath = newChildPath,
                Node = new NodeDto
                {
                    Id = oldChild.Node.Id,
                    ParentId = movedRoot.Node.Id,
                    Name = "Source",
                },
            };
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(oldFilePath, localContentHash, remoteFileId, localFile.SizeBytes);
            baselineRemote.NodeId = oldChild.Node.Id;
            localFile.LastWriteUtc = baselineRemote.UpdatedAt;
            NodeFileManifestDto movedRemote = RemoteFile(newFilePath, localContentHash, remoteFileId, localFile.SizeBytes);
            movedRemote.NodeId = movedChild.Node.Id;
            RemoteTreeSnapshot remoteTree = RemoteTree(movedRemote);
            remoteTree.Directories.AddRange([targetParent, movedRoot, movedChild]);
            var scanner = new FakeLocalFileScanner(localFile)
            {
                ContentHashFactory = file =>
                {
                    Assert.That(file.RelativePath, Is.EqualTo(oldFilePath));
                    return localContentHash;
                },
                Directories =
                {
                    LocalDirectory(targetParentPath),
                    LocalDirectory(oldRootPath),
                    LocalDirectory(oldChildPath),
                },
            };
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter
            {
                HydrationState = SyncPlaceholderHydrationState.Hydrated,
                LocalLastWriteUtc = baselineRemote.UpdatedAt.AddMinutes(1),
                LocalSizeBytes = localFile.SizeBytes,
            };
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertDirectoryBaselineAsync(stateStore, targetParentPath, targetParent.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldRootPath, oldRoot.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldChildPath, oldChild.Node);
            await InsertBaselineAsync(stateStore, oldFilePath, localContentHash, baselineRemote, localFile.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths(
                    [
                        oldRootPath,
                        oldChildPath,
                        oldFilePath,
                        newRootPath,
                        newChildPath,
                        newFilePath,
                    ]),
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            string targetFilePath = Path.Combine(_root, newFilePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldRootPath)), Is.False);
                Assert.That(File.Exists(targetFilePath), Is.True);
                Assert.That(File.ReadAllText(targetFilePath), Is.EqualTo(content));
                Assert.That(
                    state.Select(entry => entry.RelativePath),
                    Is.EqualTo(new[] { targetParentPath, newRootPath, newChildPath, newFilePath }));
                Assert.That(state.Single(entry => entry.RelativePath == newRootPath).RemoteNodeId, Is.EqualTo(oldRoot.Node.Id));
                Assert.That(state.Single(entry => entry.RelativePath == newChildPath).RemoteNodeId, Is.EqualTo(oldChild.Node.Id));
                Assert.That(state.Single(entry => entry.RelativePath == newFilePath).RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(
                    state.Single(entry => entry.RelativePath == newFilePath).LocalLastWriteUtc,
                    Is.EqualTo(placeholderWriter.LocalLastWriteUtc));
                Assert.That(
                    state.Single(entry => entry.RelativePath == newFilePath).LocalSizeBytes,
                    Is.EqualTo(placeholderWriter.LocalSizeBytes));
                Assert.That(scanner.ContentHashCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { newFilePath }));
                Assert.That(
                    placeholderWriter.CompletedDirectoryTreeRequests.Single().Select(request => request.RelativePath),
                    Is.EqualTo(new[] { newRootPath, newChildPath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    result.Activities.Select(activity => activity.Kind),
                    Is.EqualTo(new[] { SyncActivityKind.Moved, SyncActivityKind.Converged }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRemovesRemoteDeletedDirectorySubtreeInOnePass()
        {
            const string rootPath = "DeleteTarget";
            const string childPath = "DeleteTarget/Child";
            const string filePath = "DeleteTarget/Child/data.bin";
            const string content = "unchanged hydrated content";
            WriteFile(filePath, content);
            LocalFileSnapshot localFile = LocalFile(filePath, content);
            localFile.IsCloudFilesPlaceholder = true;
            RemoteDirectorySnapshot remoteRoot = RemoteDirectory(rootPath);
            RemoteDirectorySnapshot remoteChild = RemoteDirectory(childPath, remoteRoot.Node.Id);
            NodeFileManifestDto baselineRemote = RemoteFile(filePath, localFile.ContentHash, sizeBytes: localFile.SizeBytes);
            baselineRemote.NodeId = remoteChild.Node.Id;
            var scanner = new FakeLocalFileScanner(localFile)
            {
                Directories =
                {
                    LocalDirectory(rootPath),
                    LocalDirectory(childPath),
                },
            };
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, remoteRoot.Node);
            await InsertDirectoryBaselineAsync(stateStore, childPath, remoteChild.Node);
            await InsertBaselineAsync(stateStore, filePath, localFile.ContentHash, baselineRemote, localFile.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath, childPath, filePath]),
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, rootPath)), Is.False);
                Assert.That(state, Is.Empty);
                Assert.That(
                    result.Activities.Select(activity => (activity.Kind, activity.RelativePath)),
                    Is.EqualTo(new[]
                    {
                        (SyncActivityKind.DeletedLocal, filePath),
                        (SyncActivityKind.DeletedLocal, childPath),
                        (SyncActivityKind.DeletedLocal, rootPath),
                    }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PropagatesRemoteEmptyDirectoryRenameAsCreateAndDelete()
        {
            const string oldPath = "Projects";
            const string newPath = "ProjectsRenamed";
            Directory.CreateDirectory(Path.Combine(_root, oldPath));
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteDirectorySnapshot newRemoteDirectory = RemoteDirectory(newPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(newRemoteDirectory);
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(oldPath),
                },
            };
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldPath)), Is.False);
                Assert.That(Directory.Exists(Path.Combine(_root, newPath)), Is.True);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(state[0].RemoteNodeId, Is.EqualTo(newRemoteDirectory.Node.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded, SyncActivityKind.DeletedLocal }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PropagatesLocalEmptyDirectoryMoveAsCreateAndDelete()
        {
            const string parentPath = "Archive";
            const string oldPath = "Projects";
            const string newPath = "Archive/Projects";
            RemoteDirectorySnapshot remoteParent = RemoteDirectory(parentPath);
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteParent);
            remoteTree.Directories.Add(oldRemoteDirectory);
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(parentPath),
                    LocalDirectory(newPath),
                },
            };
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, parentPath, remoteParent.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates, Has.Count.EqualTo(1));
                Assert.That(remoteDirectories.Creates[0].ParentNodeId, Is.EqualTo(remoteParent.Node.Id));
                Assert.That(remoteDirectories.Creates[0].Name, Is.EqualTo("Projects"));
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (oldRemoteDirectory.Node.Id, false) }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { parentPath, newPath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.DeletedRemote }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PropagatesRemoteEmptyDirectoryMoveAsCreateAndDelete()
        {
            const string parentPath = "Archive";
            const string oldPath = "Projects";
            const string newPath = "Archive/Projects";
            Directory.CreateDirectory(Path.Combine(_root, parentPath));
            Directory.CreateDirectory(Path.Combine(_root, oldPath));
            RemoteDirectorySnapshot remoteParent = RemoteDirectory(parentPath);
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteDirectorySnapshot movedRemoteDirectory = RemoteDirectory(newPath, remoteParent.Node.Id);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteParent);
            remoteTree.Directories.Add(movedRemoteDirectory);
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(parentPath),
                    LocalDirectory(oldPath),
                },
            };
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, parentPath, remoteParent.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldPath)), Is.False);
                Assert.That(Directory.Exists(Path.Combine(_root, newPath.Replace('/', Path.DirectorySeparatorChar))), Is.True);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { parentPath, newPath }));
                Assert.That(state.Single(entry => entry.RelativePath == newPath).RemoteNodeId, Is.EqualTo(movedRemoteDirectory.Node.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded, SyncActivityKind.DeletedLocal }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesBothDirectoryRenamesWhenLocalAndRemoteRenameDiverge()
        {
            const string oldPath = "Projects";
            const string localRenamePath = "Projects Local";
            const string remoteRenamePath = "Projects Remote";
            Directory.CreateDirectory(Path.Combine(_root, localRenamePath));
            RemoteDirectorySnapshot baselineRemoteDirectory = RemoteDirectory(oldPath);
            RemoteDirectorySnapshot remoteRenamedDirectory = RemoteDirectory(remoteRenamePath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteRenamedDirectory);
            var scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(localRenamePath),
                },
            };
            var remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, baselineRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldPath)), Is.False);
                Assert.That(Directory.Exists(Path.Combine(_root, localRenamePath)), Is.True);
                Assert.That(Directory.Exists(Path.Combine(_root, remoteRenamePath)), Is.True);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { localRenamePath }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { localRenamePath, remoteRenamePath }));
                Assert.That(state.Single(entry => entry.RelativePath == localRenamePath).RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(state.Single(entry => entry.RelativePath == remoteRenamePath).RemoteNodeId, Is.EqualTo(remoteRenamedDirectory.Node.Id));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Downloaded }));
            });
        }

        [Test]
        public async Task RunOnceAsync_UploadsUnicodeNamedLocalFileAndStoresBaseline()
        {
            const string relativePath = "Документы/設計-notes.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "unicode-local-content");
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RelativePath, Is.EqualTo(relativePath));
                Assert.That(entry.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_UploadsMixedUnicodeNamedLocalFileWithNormalizedBaseline()
        {
            const string localRelativePath = "Mixed/Cafe\u0301-\u05d3\u05d5\u05d7-\ud83d\udcc4.txt";
            const string normalizedRelativePath = "Mixed/Caf\u00e9-\u05d3\u05d5\u05d7-\ud83d\udcc4.txt";
            LocalFileSnapshot local = LocalFile(localRelativePath, "mixed-unicode-local-content");
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", normalizedRelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(normalizedRelativePath));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RelativePath, Is.EqualTo(normalizedRelativePath));
                Assert.That(entry.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DownloadsUnicodeNamedRemoteFileAndStoresBaseline()
        {
            const string relativePath = "Документы/設計-remote.txt";
            byte[] content = Encoding.UTF8.GetBytes("unicode-remote-content");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(content), sizeBytes: content.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = content;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, "Документы", "設計-remote.txt")), Is.EqualTo("unicode-remote-content"));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RelativePath, Is.EqualTo(relativePath));
                Assert.That(entry.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_UploadsLocalChangeWhenRemoteBaselineIsUnchanged()
        {
            LocalFileSnapshot local = LocalFile("changed.txt", "local-new");
            NodeFileManifestDto remote = RemoteFile("changed.txt", HashText("old"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "changed.txt", HashText("old"), remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "changed.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile!.Id, Is.EqualTo(remote.Id));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotUpdateBaselineWhenRemoteUploadFails()
        {
            string relativePath = "upload-fails.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.UploadFailureIds.Add(remote.Id);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText("old"), remote);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(HashText("old")));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(remoteFiles.Uploads, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_RecoversAfterRemoteUploadBeforeBaselineUpdate()
        {
            string relativePath = "uploaded-before-baseline.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var durableStore = new SqliteSyncStateStore(_databasePath);
            var failingStore = new FailingUpsertStateStore(durableStore);
            SyncEngine firstRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            NodeFileManifestDto uploaded = remoteFiles.Uploads.Single().ReturnedFile;
            SyncEngine secondRun = new(scanner, new FakeRemoteTreeCrawler(RemoteTree(uploaded)), remoteFiles, new SqliteSyncStateStore(_databasePath));
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(uploaded.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(uploaded.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_ReusesSharedStateAcrossSequentialClientSurfaces()
        {
            const string relativePath = "sequential-surface.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "desktop-local");
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var desktopStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine desktopRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                desktopStateStore);

            SyncRunResult firstResult = await desktopRun.RunOnceAsync(Pair());

            NodeFileManifestDto uploaded = remoteFiles.Uploads.Single().ReturnedFile;
            var cliStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine cliRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(uploaded)),
                remoteFiles,
                cliStateStore);
            SyncRunResult secondResult = await cliRun.RunOnceAsync(Pair());

            byte[] remoteUpdateContent = Encoding.UTF8.GetBytes("remote-after-cli");
            NodeFileManifestDto remoteUpdate = RemoteFile(
                relativePath,
                Hash(remoteUpdateContent),
                uploaded.Id,
                remoteUpdateContent.Length);
            remoteFiles.Downloads[uploaded.Id] = remoteUpdateContent;
            var restartedDesktopStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine restartedDesktopRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(remoteUpdate)),
                remoteFiles,
                restartedDesktopStateStore);
            SyncRunResult thirdResult = await restartedDesktopRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await restartedDesktopStateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(firstResult.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(secondResult.Activities, Is.Empty);
                Assert.That(thirdResult.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-after-cli"));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remoteUpdate.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remoteUpdate.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(uploaded.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_CliInterruptedUploadCanBeRecoveredByDesktopSurface()
        {
            const string relativePath = "cli-interrupted-upload.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "cli-local-before-crash");
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var durableStore = new SqliteSyncStateStore(_databasePath);
            var cliCrashStore = new FailingUpsertStateStore(durableStore);
            SyncEngine cliRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                cliCrashStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await cliRun.RunOnceAsync(Pair()));

            NodeFileManifestDto uploaded = remoteFiles.Uploads.Single().ReturnedFile;
            SyncEngine desktopRecoveryRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(uploaded)),
                remoteFiles,
                new SqliteSyncStateStore(_databasePath));
            SyncRunResult result = await desktopRecoveryRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(uploaded.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(uploaded.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_DesktopInterruptedDownloadCanBeRecoveredByCliSurface()
        {
            const string relativePath = "desktop-interrupted-download.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote content before desktop crash");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            var durableStore = new SqliteSyncStateStore(_databasePath);
            var desktopCrashStore = new FailingUpsertStateStore(durableStore);
            SyncEngine desktopRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                desktopCrashStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await desktopRun.RunOnceAsync(Pair()));

            LocalFileSnapshot downloadedLocal = new()
            {
                RelativePath = relativePath,
                FullPath = Path.Combine(_root, relativePath),
                ContentHash = remote.ContentHash,
                SizeBytes = remoteContent.Length,
                LastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(_root, relativePath)),
            };
            SyncEngine cliRecoveryRun = new(
                new FakeLocalFileScanner(downloadedLocal),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                new SqliteSyncStateStore(_databasePath));
            SyncRunResult result = await cliRecoveryRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.ReadAllBytes(Path.Combine(_root, relativePath)), Is.EqualTo(remoteContent));
                Assert.That(result.Activities, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_RecoversAfterTransientUploadFailureWithoutStaleBaseline()
        {
            string relativePath = "network-drop-upload.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "local");
            var scanner = new FakeLocalFileScanner(local);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.UploadFailureRelativePaths.Add(relativePath);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            SyncStateEntry? failedEntry = await stateStore.GetAsync("pair-a", relativePath);
            remoteFiles.UploadFailureRelativePaths.Clear();
            SyncEngine secondRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? recoveredEntry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(failedEntry, Is.Null);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(recoveredEntry, Is.Not.Null);
                Assert.That(recoveredEntry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(recoveredEntry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_SkipsChangedLocalFileDuringUploadAndContinuesPass()
        {
            LocalFileSnapshot volatileLocal = LocalFile("hot/volatile.txt", "first local content");
            LocalFileSnapshot stableLocal = LocalFile("hot/stable.txt", "stable local content");
            var scanner = new FakeLocalFileScanner(volatileLocal, stableLocal);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.LocalUnavailableUploadRelativePaths.Add(volatileLocal.RelativePath);
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? volatileEntry = await stateStore.GetAsync("pair-a", volatileLocal.RelativePath);
            SyncStateEntry? stableEntry = await stateStore.GetAsync("pair-a", stableLocal.RelativePath);
            SyncActivity volatileActivity = result.Activities.Single(activity => activity.RelativePath == volatileLocal.RelativePath);
            SyncActivity stableActivity = result.Activities.Single(activity => activity.RelativePath == stableLocal.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Has.Count.EqualTo(2));
                Assert.That(volatileActivity.Kind, Is.EqualTo(SyncActivityKind.Skipped));
                Assert.That(volatileActivity.RequiresUserAction, Is.False);
                Assert.That(volatileActivity.Details, Does.Contain("changed during upload"));
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { volatileLocal.RelativePath }));
                Assert.That(stableActivity.Kind, Is.EqualTo(SyncActivityKind.Uploaded));
                Assert.That(remoteFiles.Uploads.Select(static upload => upload.RelativePath), Is.EqualTo(new[] { stableLocal.RelativePath }));
                Assert.That(volatileEntry, Is.Null);
                Assert.That(stableEntry, Is.Not.Null);
                Assert.That(stableEntry!.LocalContentHash, Is.EqualTo(stableLocal.ContentHash));
                Assert.That(stableEntry.RemoteContentHash, Is.EqualTo(stableLocal.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DefersFreshLocalUploadUntilQuietWindow()
        {
            LocalFileSnapshot freshLocal = LocalFile("hot/fresh.txt", "fresh local content");
            freshLocal.LastWriteUtc = DateTime.UtcNow;
            var scanner = new FakeLocalFileScanner(freshLocal);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);
            var options = new SyncRunOptions { MinimumLocalUploadAge = TimeSpan.FromMinutes(5) };

            SyncRunResult firstResult = await engine.RunOnceAsync(Pair(), options);
            SyncStateEntry? deferredEntry = await stateStore.GetAsync("pair-a", freshLocal.RelativePath);
            freshLocal.LastWriteUtc = DateTime.UtcNow.AddMinutes(-10);
            SyncRunResult secondResult = await engine.RunOnceAsync(Pair(), options);

            SyncStateEntry? uploadedEntry = await stateStore.GetAsync("pair-a", freshLocal.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(firstResult.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped }));
                Assert.That(firstResult.DeferredLocalPaths, Is.EqualTo(new[] { freshLocal.RelativePath }));
                Assert.That(firstResult.Activities.Single().Details, Does.Contain("quiet window"));
                Assert.That(deferredEntry, Is.Null);
                Assert.That(secondResult.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(secondResult.HasDeferredLocalPaths, Is.False);
                Assert.That(remoteFiles.Uploads.Select(static upload => upload.RelativePath), Is.EqualTo(new[] { freshLocal.RelativePath }));
                Assert.That(uploadedEntry, Is.Not.Null);
                Assert.That(uploadedEntry!.LocalContentHash, Is.EqualTo(freshLocal.ContentHash));
                Assert.That(uploadedEntry.RemoteContentHash, Is.EqualTo(freshLocal.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_UploadsAccumulatedLocalChangesAfterTransientUploadFailure()
        {
            LocalFileSnapshot first = LocalFile("offline/first.txt", "first offline local content");
            LocalFileSnapshot second = LocalFile("offline/second.txt", "second offline local content");
            var scanner = new FakeLocalFileScanner(first, second);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.UploadFailureRelativePaths.Add(first.RelativePath);
            remoteFiles.UploadFailureRelativePaths.Add(second.RelativePath);
            var stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            remoteFiles.UploadFailureRelativePaths.Clear();
            SyncEngine secondRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());
            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", first.RelativePath);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", second.RelativePath);

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Uploaded }));
                Assert.That(remoteFiles.Uploads.Select(static upload => upload.RelativePath), Is.EqualTo(new[] { first.RelativePath, second.RelativePath }));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(firstEntry!.LocalContentHash, Is.EqualTo(first.ContentHash));
                Assert.That(firstEntry.RemoteContentHash, Is.EqualTo(first.ContentHash));
                Assert.That(secondEntry, Is.Not.Null);
                Assert.That(secondEntry!.LocalContentHash, Is.EqualTo(second.ContentHash));
                Assert.That(secondEntry.RemoteContentHash, Is.EqualTo(second.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DownloadsRemoteChangeWhenLocalBaselineIsUnchanged()
        {
            string relativePath = "changed-down.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotUpdateBaselineWhenRemoteDownloadFails()
        {
            string relativePath = "download-fails.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            NodeFileManifestDto remote = RemoteFile(
                relativePath,
                HashText("remote-new"),
                sizeBytes: Encoding.UTF8.GetByteCount("remote-new"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.DownloadFailureIds.Add(remote.Id);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("old"));
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_RecoversAfterTransientDownloadFailureWithoutStalePartial()
        {
            string relativePath = "network-drop-download.txt";
            WriteFile(relativePath, "local-before-server-error");
            LocalFileSnapshot local = LocalFile(relativePath, "local-before-server-error");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PartialDownloadFailureIds.Add(remote.Id);
            remoteFiles.Downloads[remote.Id] = remoteContent;
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(
                stateStore,
                relativePath,
                local.ContentHash,
                RemoteFile(relativePath, local.ContentHash, remote.Id));
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                stateStore);

            Assert.ThrowsAsync<CottonApiException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            string localPath = Path.Combine(_root, relativePath);
            SyncStateEntry? failedEntry = await stateStore.GetAsync("pair-a", relativePath);
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(localPath), Is.EqualTo("local-before-server-error"));
                Assert.That(failedEntry, Is.Not.Null);
                Assert.That(failedEntry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(failedEntry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
            });

            remoteFiles.PartialDownloadFailureIds.Clear();
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? recoveredEntry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(File.ReadAllText(localPath), Is.EqualTo("remote"));
                Assert.That(recoveredEntry, Is.Not.Null);
                Assert.That(recoveredEntry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(recoveredEntry.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(recoveredEntry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DownloadsAccumulatedRemoteChangesAfterTransientDownloadFailure()
        {
            byte[] firstContent = Encoding.UTF8.GetBytes("first remote content");
            byte[] secondContent = Encoding.UTF8.GetBytes("second remote content");
            NodeFileManifestDto first = RemoteFile("offline/remote-first.txt", Hash(firstContent), sizeBytes: firstContent.Length);
            NodeFileManifestDto second = RemoteFile("offline/remote-second.txt", Hash(secondContent), sizeBytes: secondContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.DownloadFailureIds.Add(first.Id);
            remoteFiles.DownloadFailureIds.Add(second.Id);
            remoteFiles.Downloads[first.Id] = firstContent;
            remoteFiles.Downloads[second.Id] = secondContent;
            var stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(first, second)),
                remoteFiles,
                stateStore);

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            remoteFiles.DownloadFailureIds.Clear();
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(first, second)),
                remoteFiles,
                stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());
            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", first.Metadata["relativePath"]);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", second.Metadata["relativePath"]);

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded, SyncActivityKind.Downloaded }));
                Assert.That(File.ReadAllText(Path.Combine(_root, "offline", "remote-first.txt")), Is.EqualTo("first remote content"));
                Assert.That(File.ReadAllText(Path.Combine(_root, "offline", "remote-second.txt")), Is.EqualTo("second remote content"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(firstEntry!.RemoteContentHash, Is.EqualTo(first.ContentHash));
                Assert.That(secondEntry, Is.Not.Null);
                Assert.That(secondEntry!.RemoteContentHash, Is.EqualTo(second.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_RejectsDownloadedContentThatDoesNotMatchManifest()
        {
            string relativePath = "download-corrupt.txt";
            byte[] expectedContent = Encoding.UTF8.GetBytes("complete remote file");
            byte[] partialContent = Encoding.UTF8.GetBytes("partial");
            NodeFileManifestDto remote = RemoteFile(
                relativePath,
                Hash(expectedContent),
                sizeBytes: expectedContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = partialContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);

            InvalidDataException? exception = Assert.ThrowsAsync<InvalidDataException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(entry, Is.Null);
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_RecoversAfterRemoteDownloadBeforeBaselineUpdate()
        {
            string relativePath = "downloaded-before-baseline.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            var durableStore = new SqliteSyncStateStore(_databasePath);
            var failingStore = new FailingUpsertStateStore(durableStore);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            IReadOnlyList<SyncStateEntry> entriesAfterCrash = await durableStore.LoadPairAsync("pair-a");
            LocalFileSnapshot downloadedLocal = LocalFile(relativePath, "remote-new");
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(downloadedLocal),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                durableStore);

            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-new"));
                Assert.That(entriesAfterCrash, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }

        [Test]
        public async Task RunOnceAsync_DeletesRemoteOnlyWhenBaselineKnowsLocalDelete()
        {
            NodeFileManifestDto remote = RemoteFile("delete-remote.txt", HashText("old"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "delete-remote.txt", remote.ContentHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "delete-remote.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_CanBypassRemoteTrashWhenExplicitlyConfigured()
        {
            NodeFileManifestDto remote = RemoteFile("delete-remote-permanent.txt", HashText("old"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "delete-remote-permanent.txt", remote.ContentHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { DeleteRemotePermanently = true });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "delete-remote-permanent.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, true, remote.ETag) }));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotDeleteBaselineWhenRemoteDeleteFails()
        {
            string relativePath = "delete-remote-fails.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.DeleteFailureIds.Add(remote.Id);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, remote.ContentHash, remote);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_RecoversAfterRemoteDeleteBeforeBaselineDelete()
        {
            string relativePath = "remote-deleted-before-baseline.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var durableStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(durableStore, relativePath, remote.ContentHash, remote);
            var failingStore = new FailingDeleteStateStore(durableStore);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            SyncStateEntry? staleEntry = await durableStore.GetAsync("pair-a", relativePath);
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                durableStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(staleEntry, Is.Not.Null);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_RecoversAfterLocalDeleteBeforeBaselineDelete()
        {
            string relativePath = "local-deleted-before-baseline.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            NodeFileManifestDto remote = RemoteFile(relativePath, local.ContentHash);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var durableStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(durableStore, relativePath, local.ContentHash, remote);
            var failingStore = new FailingDeleteStateStore(durableStore);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            SyncStateEntry? staleEntry = await durableStore.GetAsync("pair-a", relativePath);
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                durableStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(staleEntry, Is.Not.Null);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_BlocksRemoteDeletesOverRunLimit()
        {
            NodeFileManifestDto firstRemote = RemoteFile("a.txt", HashText("old-a"));
            NodeFileManifestDto secondRemote = RemoteFile("b.txt", HashText("old-b"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(firstRemote, secondRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "a.txt", firstRemote.ContentHash, firstRemote);
            await InsertBaselineAsync(stateStore, "b.txt", secondRemote.ContentHash, secondRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", "a.txt");
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", "b.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(result.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_DownloadsRemoteFileInsteadOfDeletingWhenBaselineIsMissing()
        {
            byte[] content = Encoding.UTF8.GetBytes("no-baseline-remote");
            NodeFileManifestDto remote = RemoteFile("safe-download.txt", Hash(content), sizeBytes: content.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = content;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out _);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(File.ReadAllText(Path.Combine(_root, "safe-download.txt")), Is.EqualTo("no-baseline-remote"));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
            });
        }

        [Test]
        public async Task RunOnceAsync_DeletesLocalWhenBaselineKnowsRemoteDelete()
        {
            string relativePath = "delete-local.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, local.ContentHash);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_BlocksLocalDeletesOverRunLimit()
        {
            WriteFile("a.txt", "old-a");
            WriteFile("b.txt", "old-b");
            LocalFileSnapshot firstLocal = LocalFile("a.txt", "old-a");
            LocalFileSnapshot secondLocal = LocalFile("b.txt", "old-b");
            NodeFileManifestDto firstRemote = RemoteFile("a.txt", firstLocal.ContentHash);
            NodeFileManifestDto secondRemote = RemoteFile("b.txt", secondLocal.ContentHash);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(firstLocal, secondLocal),
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "a.txt", firstLocal.ContentHash, firstRemote);
            await InsertBaselineAsync(stateStore, "b.txt", secondLocal.ContentHash, secondRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumLocalDeletesPerRun = 1 });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", "a.txt");
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", "b.txt");
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, "a.txt")), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "b.txt")), Is.True);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(result.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesBothVersionsWhenLocalAndRemoteChanged()
        {
            string relativePath = "conflict.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText("old"), RemoteFile(relativePath, HashText("old"), remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(result.Activities[0].Details, Does.Contain("Cotton conflict"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.LocalContentHash, Is.Not.EqualTo(entry.RemoteContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesBothVersionsWhenNearSimultaneousLocalAndRemoteEditsDiverge()
        {
            string relativePath = "near-simultaneous-conflict.txt";
            Guid remoteId = Guid.NewGuid();
            string baselineContent = "baseline";
            DateTime baselineUtc = new(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc);
            DateTime localEditUtc = baselineUtc.AddSeconds(1);
            DateTime remoteEditUtc = baselineUtc.AddSeconds(3);
            WriteFile(relativePath, "local-within-window");
            LocalFileSnapshot local = LocalFile(relativePath, "local-within-window");
            local.LastWriteUtc = localEditUtc;
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-within-window");
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText(baselineContent), remoteId, baselineContent.Length);
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), remoteId, remoteContent.Length);
            remote.UpdatedAt = remoteEditUtc;
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText(baselineContent), baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That((remoteEditUtc - localEditUtc).TotalSeconds, Is.EqualTo(2));
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-within-window"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-within-window"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalLastWriteUtc, Is.EqualTo(localEditUtc));
                Assert.That(entry.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.LocalContentHash, Is.Not.EqualTo(entry.RemoteContentHash));
            });
        }

        [TestCase(MatrixFileState.Missing, MatrixFileState.Missing, 0)]
        [TestCase(MatrixFileState.Missing, MatrixFileState.Baseline, (int)SyncActivityKind.DeletedRemote)]
        [TestCase(MatrixFileState.Missing, MatrixFileState.Changed, (int)SyncActivityKind.Conflict)]
        [TestCase(MatrixFileState.Baseline, MatrixFileState.Missing, (int)SyncActivityKind.DeletedLocal)]
        [TestCase(MatrixFileState.Baseline, MatrixFileState.Baseline, 0)]
        [TestCase(MatrixFileState.Baseline, MatrixFileState.Changed, (int)SyncActivityKind.Downloaded)]
        [TestCase(MatrixFileState.Changed, MatrixFileState.Missing, (int)SyncActivityKind.Conflict)]
        [TestCase(MatrixFileState.Changed, MatrixFileState.Baseline, (int)SyncActivityKind.Uploaded)]
        [TestCase(MatrixFileState.Changed, MatrixFileState.Changed, (int)SyncActivityKind.Conflict)]
        public async Task RunOnceAsync_ReconcilesBaselineMatrix(
            MatrixFileState localState,
            MatrixFileState remoteState,
            int expectedActivityKind)
        {
            string relativePath = $"matrix/{localState}-{remoteState}.txt";
            string baselineContent = "base";
            string localContent = localState == MatrixFileState.Changed ? "local-changed" : baselineContent;
            string remoteContent = remoteState == MatrixFileState.Changed ? "remote-changed" : baselineContent;
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText(baselineContent), remoteId);
            LocalFileSnapshot? local = CreateMatrixLocal(relativePath, localState, localContent);
            NodeFileManifestDto? remote = remoteState == MatrixFileState.Missing
                ? null
                : RemoteFile(relativePath, HashText(remoteContent), remoteId, Encoding.UTF8.GetByteCount(remoteContent));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            if (remote is not null && remoteState == MatrixFileState.Changed)
            {
                remoteFiles.Downloads[remote.Id] = Encoding.UTF8.GetBytes(remoteContent);
            }

            LocalFileSnapshot[] localFiles = local is null ? [] : [local];
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(localFiles),
                remote is null ? EmptyRemoteTree() : RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText(baselineContent), baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncActivityKind> expectedKinds = expectedActivityKind == 0
                ? []
                : [(SyncActivityKind)expectedActivityKind];
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(expectedKinds));
                AssertMatrixSideEffects(relativePath, localState, remoteState, remoteFiles);
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesBothVersionsWhenStaleUploadLosesRemoteRace()
        {
            string relativePath = "stale-upload.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto initialRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            byte[] latestRemoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto latestRemote = RemoteFile(relativePath, Hash(latestRemoteContent), remoteId, latestRemoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedUploadIds.Add(remoteId);
            remoteFiles.Downloads[remoteId] = latestRemoteContent;
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(initialRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(latestRemote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_FailsBeforeRaceConflictDownloadWhenRemoteVersionExceedsFreeSpace()
        {
            string relativePath = "stale-huge-upload.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto initialRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto latestRemote = RemoteFile(relativePath, HashText("remote-huge"), remoteId, long.MaxValue);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedUploadIds.Add(remoteId);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(initialRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote);

            LocalInsufficientDiskSpaceException? exception = Assert.ThrowsAsync<LocalInsufficientDiskSpaceException>(
                () => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("Not enough disk space"));
                Assert.That(exception?.RelativePath, Does.Contain("stale-huge-upload"));
                Assert.That(exception?.RequiredBytes, Is.EqualTo(long.MaxValue));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories), Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_RestoresRemoteVersionWhenStaleDeleteLosesRemoteRace()
        {
            string relativePath = "stale-delete.txt";
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto initialRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            byte[] latestRemoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto latestRemote = RemoteFile(relativePath, Hash(latestRemoteContent), remoteId, latestRemoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedDeleteIds.Add(remoteId);
            remoteFiles.Downloads[remoteId] = latestRemoteContent;
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(initialRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remoteId, false, initialRemote.ETag) }));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(latestRemote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(latestRemote.ContentHash));
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotDuplicateConflictCopiesWhenUnresolvedConflictIsUnchanged()
        {
            string relativePath = "conflict-stable.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-new"));
            var remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(conflictFiles, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesUnresolvedConflictWhenRemoteChangesAgain()
        {
            string relativePath = "conflict-remote-again.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-newer");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, HashText("remote-old"), remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-newer"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }

        [Test]
        public void RunOnceAsync_HonorsCancellationBeforeScanning()
        {
            var scanner = new FakeLocalFileScanner(LocalFile("cancel.txt", "cancel"));
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunOnceAsync(Pair(), cancellationToken: cancellation.Token));
            Assert.That(scanner.ScanCalls, Is.Zero);
        }

        [Test]
        public void RunOnceAsync_RejectsLocalCaseInsensitivePathCollision()
        {
            var scanner = new FakeLocalFileScanner(
                LocalFile("Case.txt", "first"),
                LocalFile("case.txt", "second"));
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);

            SyncPathCollisionException? exception = Assert.ThrowsAsync<SyncPathCollisionException>(() => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.FirstPath, Is.EqualTo("Case.txt"));
                Assert.That(exception.SecondPath, Is.EqualTo("case.txt"));
                Assert.That(exception.Message, Does.Contain("Case-insensitive path collision"));
                Assert.That(exception.Message, Does.Contain("Case.txt"));
                Assert.That(exception.Message, Does.Contain("case.txt"));
            });
        }

        [Test]
        public void RunOnceAsync_RejectsLocalFileDirectoryCaseInsensitivePathCollision()
        {
            var scanner = new FakeLocalFileScanner(LocalFile("Project", "file"));
            scanner.Directories.Add(LocalDirectory("project"));
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);

            SyncPathCollisionException? exception = Assert.ThrowsAsync<SyncPathCollisionException>(() => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.FirstPath, Is.EqualTo("project"));
                Assert.That(exception.SecondPath, Is.EqualTo("Project"));
                Assert.That(exception.Message, Does.Contain("Case-insensitive path collision"));
            });
        }

        [Test]
        public void RunOnceAsync_RejectsRemoteCaseInsensitivePathCollision()
        {
            RemoteTreeSnapshot remoteTree = RemoteTree(
                RemoteFile("Remote.txt", HashText("first")),
                RemoteFile("remote.txt", HashText("second")));
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), remoteTree, new FakeRemoteFileSynchronizer(), out _);

            SyncPathCollisionException? exception = Assert.ThrowsAsync<SyncPathCollisionException>(() => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.FirstPath, Is.EqualTo("Remote.txt"));
                Assert.That(exception.SecondPath, Is.EqualTo("remote.txt"));
                Assert.That(exception.Message, Does.Contain("Case-insensitive path collision"));
                Assert.That(exception.Message, Does.Contain("Remote.txt"));
                Assert.That(exception.Message, Does.Contain("remote.txt"));
            });
        }

        [Test]
        public void RunOnceAsync_RejectsRemoteFileDirectoryCaseInsensitivePathCollision()
        {
            RemoteTreeSnapshot remoteTree = RemoteTree(RemoteFile("Remote", HashText("file")));
            remoteTree.Directories.Add(RemoteDirectory("remote"));
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), remoteTree, new FakeRemoteFileSynchronizer(), out _);

            SyncPathCollisionException? exception = Assert.ThrowsAsync<SyncPathCollisionException>(() => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.FirstPath, Is.EqualTo("remote"));
                Assert.That(exception.SecondPath, Is.EqualTo("Remote"));
                Assert.That(exception.Message, Does.Contain("Case-insensitive path collision"));
            });
        }

        [Test]
        public async Task RunOnceAsync_IgnoresRemoteMetadataPathsAtEngineBoundary()
        {
            NodeFileManifestDto remote = RemoteFile(".cotton-sync/remote-file.txt", HashText("remote"));
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = ".cotton-sync/remote-file.txt",
                Kind = SyncEntryKind.File,
                RemoteFileId = remote.Id,
                RemoteNodeId = remote.NodeId,
                RemoteContentHash = remote.ContentHash,
                RemoteETag = remote.ETag,
            });

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> entries = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entries, Is.Empty);
                Assert.That(File.Exists(Path.Combine(_root, ".cotton-sync", "remote-file.txt")), Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesIgnoresRemoteMetadataPathsAtEngineBoundary()
        {
            NodeFileManifestDto remote = RemoteFile(".cotton-sync/remote-placeholder.txt", HashText("remote"), sizeBytes: 1024);
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = ".cotton-sync/remote-placeholder.txt",
                Kind = SyncEntryKind.File,
                RemoteFileId = remote.Id,
                RemoteNodeId = remote.NodeId,
                RemoteContentHash = remote.ContentHash,
                RemoteETag = remote.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
            });

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            IReadOnlyList<SyncStateEntry> entries = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entries, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(File.Exists(Path.Combine(_root, ".cotton-sync", "remote-placeholder.txt")), Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_DoesNotLeakStateAcrossSyncPairsSharingDatabaseAndRelativePath()
        {
            LocalFileSnapshot pairALocal = LocalFile("shared.txt", "pair-a-local");
            var remoteFiles = new FakeRemoteFileSynchronizer();
            var stateStore = new SqliteSyncStateStore(_databasePath);
            await stateStore.InitializeAsync();
            var pairBRemoteFileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-b",
                RelativePath = "shared.txt",
                Kind = SyncEntryKind.File,
                LocalContentHash = "pair-b-local-hash",
                RemoteContentHash = "pair-b-remote-hash",
                RemoteFileId = pairBRemoteFileId,
                RemoteNodeId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                RemoteETag = "pair-b-etag",
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
            SyncEngine engine = new(
                new FakeLocalFileScanner(pairALocal),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? pairAEntry = await stateStore.GetAsync("pair-a", "shared.txt");
            SyncStateEntry? pairBEntry = await stateStore.GetAsync("pair-b", "shared.txt");
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo("shared.txt"));
                Assert.That(pairAEntry, Is.Not.Null);
                Assert.That(pairAEntry!.LocalContentHash, Is.EqualTo(pairALocal.ContentHash));
                Assert.That(pairBEntry, Is.Not.Null);
                Assert.That(pairBEntry!.LocalContentHash, Is.EqualTo("pair-b-local-hash"));
                Assert.That(pairBEntry.RemoteContentHash, Is.EqualTo("pair-b-remote-hash"));
                Assert.That(pairBEntry.RemoteFileId, Is.EqualTo(pairBRemoteFileId));
            });
        }

        private SyncEngine CreateEngine(
            ILocalFileScanner scanner,
            RemoteTreeSnapshot remoteTree,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            ILogger<SyncEngine>? logger = null,
            IRemoteFilePlaceholderWriter? remoteFilePlaceholderWriter = null)
        {
            return CreateEngineWithLogger(scanner, remoteFiles, out stateStore, logger, remoteFilePlaceholderWriter, remoteTree);
        }

        private SyncEngine CreateEngine(
            ILocalFileScanner scanner,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            params RemoteTreeSnapshot[] remoteTrees)
        {
            return CreateEngineWithLogger(scanner, remoteFiles, out stateStore, null, null, remoteTrees);
        }

        private SyncEngine CreateEngineWithLogger(
            ILocalFileScanner scanner,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            ILogger<SyncEngine>? logger,
            IRemoteFilePlaceholderWriter? remoteFilePlaceholderWriter,
            params RemoteTreeSnapshot[] remoteTrees)
        {
            stateStore = new SqliteSyncStateStore(_databasePath);
            return new SyncEngine(
                scanner,
                new FakeRemoteTreeCrawler(remoteTrees),
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: remoteFilePlaceholderWriter,
                logger: logger);
        }

        private SyncEngine CreateEngine(
            ILocalFileScanner scanner,
            RemoteTreeSnapshot remoteTree,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            FakeRemoteDirectorySynchronizer remoteDirectories,
            ILogger<SyncEngine>? logger = null)
        {
            stateStore = new SqliteSyncStateStore(_databasePath);
            return new SyncEngine(
                scanner,
                new FakeRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                logger: logger);
        }

        private SyncPair Pair(SyncPairMaterializationMode materializationMode = SyncPairMaterializationMode.FullMirror)
        {
            return new SyncPair
            {
                SyncPairId = "pair-a",
                LocalRootPath = _root,
                RemoteRootNodeId = _remoteRootNodeId,
                MaterializationMode = materializationMode,
            };
        }

        private async Task InsertBaselineAsync(
            SqliteSyncStateStore stateStore,
            string relativePath,
            string localContentHash,
            NodeFileManifestDto remoteFile,
            long? localSizeBytes = null)
        {
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = localContentHash,
                LocalLastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
                LocalSizeBytes = localSizeBytes,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = remoteFile.Id,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
        }

        private async Task InsertPlaceholderBaselineAsync(
            SqliteSyncStateStore stateStore,
            string relativePath,
            NodeFileManifestDto remoteFile,
            SyncPlaceholderHydrationState hydrationState = SyncPlaceholderHydrationState.RemoteOnly)
        {
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = remoteFile.Id,
                RemoteSizeBytes = remoteFile.SizeBytes,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = hydrationState,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
        }

        private async Task InsertDirectoryBaselineAsync(
            SqliteSyncStateStore stateStore,
            string relativePath,
            NodeDto remoteNode)
        {
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = remoteNode.Id,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
        }

        private LocalFileSnapshot LocalFile(string relativePath, string content)
        {
            return new LocalFileSnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                FullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                ContentHash = HashText(content),
                SizeBytes = Encoding.UTF8.GetByteCount(content),
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
            };
        }

        private LocalFileSnapshot CloudFilesPlaceholderLocal(string relativePath, long sizeBytes)
        {
            return new LocalFileSnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                FullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                ContentHash = string.Empty,
                SizeBytes = sizeBytes,
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 2, 0, DateTimeKind.Utc),
                IsCloudFilesPlaceholder = true,
                IsCloudFilesOnlineOnlyPlaceholder = true,
            };
        }

        private LocalDirectorySnapshot LocalDirectory(string relativePath)
        {
            return new LocalDirectorySnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                FullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            };
        }

        private void WriteFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetLastWriteTimeUtc(fullPath, new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc));
        }

        private LocalFileSnapshot? CreateMatrixLocal(string relativePath, MatrixFileState state, string content)
        {
            if (state == MatrixFileState.Missing)
            {
                return null;
            }

            WriteFile(relativePath, content);
            return LocalFile(relativePath, content);
        }

        private void AssertMatrixSideEffects(
            string relativePath,
            MatrixFileState localState,
            MatrixFileState remoteState,
            FakeRemoteFileSynchronizer remoteFiles)
        {
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (localState == MatrixFileState.Missing && remoteState == MatrixFileState.Baseline)
            {
                Assert.That(remoteFiles.Deletes, Has.Count.EqualTo(1));
            }
            else if (localState == MatrixFileState.Baseline && remoteState == MatrixFileState.Missing)
            {
                Assert.That(File.Exists(fullPath), Is.False);
            }
            else if (localState == MatrixFileState.Baseline && remoteState == MatrixFileState.Changed)
            {
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("remote-changed"));
            }
            else if (localState == MatrixFileState.Changed && remoteState is MatrixFileState.Missing or MatrixFileState.Baseline)
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
            }
            else if (localState == MatrixFileState.Changed && remoteState == MatrixFileState.Changed)
            {
                string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("local-changed"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-changed"));
            }
            else if (localState == MatrixFileState.Missing && remoteState == MatrixFileState.Changed)
            {
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("remote-changed"));
            }
        }

        private RemoteTreeSnapshot EmptyRemoteTree()
        {
            return new RemoteTreeSnapshot
            {
                RootNode = new NodeDto
                {
                    Id = _remoteRootNodeId,
                    Name = "root",
                },
            };
        }

        private RemoteTreeSnapshot RemoteTree(params NodeFileManifestDto[] files)
        {
            RemoteTreeSnapshot tree = EmptyRemoteTree();
            foreach (NodeFileManifestDto file in files)
            {
                tree.Files.Add(new RemoteFileSnapshot
                {
                    RelativePath = file.Metadata["relativePath"],
                    File = file,
                });
            }

            return tree;
        }

        private RemoteDirectorySnapshot RemoteDirectory(string relativePath, Guid? parentNodeId = null)
        {
            return new RemoteDirectorySnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                Node = new NodeDto
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentNodeId ?? _remoteRootNodeId,
                    Name = relativePath.Split('/')[^1],
                },
            };
        }

        private NodeFileManifestDto RemoteFile(string relativePath, string contentHash, Guid? id = null, long sizeBytes = 1)
        {
            return new NodeFileManifestDto
            {
                Id = id ?? Guid.NewGuid(),
                CreatedAt = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 6, 2, 12, 30, 0, DateTimeKind.Utc),
                NodeId = _remoteRootNodeId,
                FileManifestId = Guid.NewGuid(),
                OriginalNodeFileId = id ?? Guid.NewGuid(),
                OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = relativePath.Split('/')[^1],
                ContentType = "text/plain",
                SizeBytes = sizeBytes,
                ContentHash = contentHash,
                ETag = "sha256-" + contentHash,
                Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath.Replace('\\', '/') },
            };
        }

        private static string HashText(string text)
        {
            return Hash(Encoding.UTF8.GetBytes(text));
        }

        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        private class FakeLocalFileScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataPathLookupScanner,
            ILocalFileContentHasher
        {
            public FakeLocalFileScanner(params LocalFileSnapshot[] files)
            {
                Files = files.ToList();
            }

            public List<LocalDirectorySnapshot> Directories { get; } = [];

            public List<LocalFileSnapshot> Files { get; }

            public int ScanCalls { get; private set; }

            public int PathLookupCalls { get; private set; }

            public int ContentHashCalls { get; private set; }

            public Func<LocalFileSnapshot, string>? ContentHashFactory { get; init; }

            public bool? LastIncludeDirectoryDescendants { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                ScanCalls++;
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Files);
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                ScanCalls++;
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Directories = Directories,
                    Files = Files,
                });
            }

            public Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
                string rootPath,
                IReadOnlyCollection<string> relativePaths,
                IProgress<LocalTreeScanProgress>? progress,
                bool includeDirectoryDescendants,
                CancellationToken cancellationToken = default)
            {
                PathLookupCalls++;
                LastIncludeDirectoryDescendants = includeDirectoryDescendants;
                var snapshot = new LocalTreeLookupSnapshot();
                var requested = relativePaths.Select(SyncPath.Normalize).ToArray();
                var requestedKeys = new HashSet<string>(
                    requested.Select(SyncPath.ToKey),
                    StringComparer.OrdinalIgnoreCase);
                foreach (LocalDirectorySnapshot directory in Directories)
                {
                    if (ContainsRequestedPath(directory.RelativePath, requestedKeys, requested, includeDirectoryDescendants))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (LocalFileSnapshot file in Files)
                {
                    if (ContainsRequestedPath(file.RelativePath, requestedKeys, requested, includeDirectoryDescendants))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }

            private static bool ContainsRequestedPath(
                string relativePath,
                IReadOnlySet<string> requestedKeys,
                IReadOnlyCollection<string> requestedPaths,
                bool includeDirectoryDescendants)
            {
                string key = SyncPath.ToKey(relativePath);
                return requestedKeys.Contains(key)
                    || requestedPaths.Any(path => IsDescendantPath(path, relativePath))
                    || includeDirectoryDescendants && requestedPaths.Any(path => IsDescendantPath(relativePath, path));
            }

            private static bool IsDescendantPath(string relativePath, string parentPath)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                string normalizedParent = SyncPath.Normalize(parentPath).TrimEnd('/');
                return normalizedPath.Length > normalizedParent.Length
                    && normalizedPath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                ContentHashCalls++;
                return Task.FromResult(ContentHashFactory?.Invoke(localFile) ?? localFile.ContentHash);
            }
        }

        private class MetadataOnlyLocalFileScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataTreeScanner,
            ILocalFileMetadataTreeProgressScanner,
            ILocalFileContentHashProgressHasher
        {
            public MetadataOnlyLocalFileScanner(params LocalFileSnapshot[] files)
            {
                Files = files.ToList();
            }

            public List<LocalFileSnapshot> Files { get; }

            public int ContentHashCalls { get; private set; }

            public bool ReportMetadataScanProgress { get; init; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Files);
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(
                string rootPath,
                IProgress<LocalTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                if (ReportMetadataScanProgress)
                {
                    progress?.Report(new LocalTreeScanProgress(0, 0, currentPath: null));
                    for (int index = 0; index < Files.Count; index++)
                    {
                        progress?.Report(new LocalTreeScanProgress(index + 1, 0, Files[index].RelativePath));
                    }

                    progress?.Report(new LocalTreeScanProgress(Files.Count, 0, currentPath: null));
                }

                return ScanTreeMetadataAsync(rootPath, cancellationToken);
            }

            public Task<string> ComputeContentHashAsync(LocalFileSnapshot localFile, CancellationToken cancellationToken = default)
            {
                return ComputeContentHashAsync(localFile, progress: null, cancellationToken);
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                IProgress<SyncTransferProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                ContentHashCalls++;
                progress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Hash,
                    localFile.RelativePath,
                    transferredBytes: 0,
                    totalBytes: localFile.SizeBytes));
                progress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Hash,
                    localFile.RelativePath,
                    localFile.SizeBytes,
                    localFile.SizeBytes,
                    isCompleted: true));
                return Task.FromResult("precomputed-content-hash");
            }
        }

        private class LookupOnlyLocalFileScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataTreeLookupScanner,
            ILocalFileContentHasher
        {
            public LookupOnlyLocalFileScanner(params LocalFileSnapshot[] files)
            {
                Files = files.ToList();
            }

            public List<LocalFileSnapshot> Files { get; }

            public int LookupScanCalls { get; private set; }

            public int MetadataTreeScanCalls { get; private set; }

            public int TreeScanCalls { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<LocalFileSnapshot>>(Files);
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                TreeScanCalls++;
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(string rootPath, CancellationToken cancellationToken = default)
            {
                MetadataTreeScanCalls++;
                return Task.FromResult(new LocalTreeSnapshot
                {
                    Files = Files,
                });
            }

            public Task<LocalTreeLookupSnapshot> ScanTreeMetadataLookupsAsync(
                string rootPath,
                IProgress<LocalTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                LookupScanCalls++;
                var snapshot = new LocalTreeLookupSnapshot();
                foreach (LocalFileSnapshot file in Files)
                {
                    snapshot.FilesByPath.Add(SyncPath.ToKey(file.RelativePath), file);
                }

                return Task.FromResult(snapshot);
            }

            public Task<string> ComputeContentHashAsync(LocalFileSnapshot localFile, CancellationToken cancellationToken = default)
            {
                return Task.FromResult("precomputed-content-hash");
            }
        }

        private class FakeRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly Queue<RemoteTreeSnapshot> _snapshots;
            private RemoteTreeSnapshot _lastSnapshot;

            public int CrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public FakeRemoteTreeCrawler(params RemoteTreeSnapshot[] snapshots)
            {
                if (snapshots.Length == 0)
                {
                    throw new ArgumentException("At least one remote snapshot is required.", nameof(snapshots));
                }

                _snapshots = new Queue<RemoteTreeSnapshot>(snapshots);
                _lastSnapshot = snapshots[0];
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                CrawlCalls++;
                return Task.FromResult(TakeNextSnapshot());
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                RemoteTreeSnapshot source = TakeNextSnapshot();
                RemoteTreeLookupSnapshot result = new()
                {
                    RootNode = source.RootNode,
                };
                foreach (RemoteDirectorySnapshot directory in source.Directories)
                {
                    if (relativePaths.Contains(directory.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        result.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in source.Files)
                {
                    if (relativePaths.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        result.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(result);
            }

            private RemoteTreeSnapshot TakeNextSnapshot()
            {
                if (_snapshots.Count > 0)
                {
                    _lastSnapshot = _snapshots.Dequeue();
                }

                return _lastSnapshot;
            }
        }

        private class FakeRemoteTreeProgressCrawler : IRemoteTreeProgressCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;
            private readonly IReadOnlyList<string> _progressPaths;

            public FakeRemoteTreeProgressCrawler(RemoteTreeSnapshot snapshot, params string[] progressPaths)
            {
                _snapshot = snapshot;
                _progressPaths = progressPaths.Length == 0
                    ? snapshot.Files.Select(file => file.RelativePath).ToList()
                    : progressPaths.ToList();
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(
                Guid rootNodeId,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                int entriesExpected = _progressPaths.Count + _snapshot.Directories.Count;
                progress?.Report(new RemoteTreeScanProgress(
                    0,
                    _snapshot.Directories.Count,
                    currentPath: null,
                    entriesExpected: entriesExpected));
                for (int index = 0; index < _progressPaths.Count; index++)
                {
                    progress?.Report(new RemoteTreeScanProgress(
                        index + 1,
                        _snapshot.Directories.Count,
                        _progressPaths[index],
                        entriesExpected: entriesExpected));
                }

                progress?.Report(new RemoteTreeScanProgress(
                    _progressPaths.Count,
                    _snapshot.Directories.Count,
                    currentPath: null,
                    entriesExpected: entriesExpected));
                return Task.FromResult(_snapshot);
            }
        }

        private sealed class BlockingStreamingRemoteTreeCrawler : IRemoteTreeStreamingCrawler
        {
            private readonly Guid _rootNodeId;
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;
            private readonly RemoteTreeSnapshot? _snapshotCrawlResult;
            private readonly int? _entriesExpected;

            public BlockingStreamingRemoteTreeCrawler(
                Guid rootNodeId,
                IReadOnlyList<RemoteFileSnapshot> files,
                RemoteTreeSnapshot? snapshotCrawlResult = null,
                int? entriesExpected = null)
            {
                _rootNodeId = rootNodeId;
                _files = files;
                _snapshotCrawlResult = snapshotCrawlResult;
                _entriesExpected = entriesExpected;
            }

            public TaskCompletionSource FirstPlaceholderStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SnapshotCrawlCalls { get; private set; }

            public int StreamingCrawlCalls { get; private set; }

            public bool FirstPlaceholderStartedBeforeStreamingCompleted { get; private set; }

            public bool StreamingCompleted { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                if (_snapshotCrawlResult is not null)
                {
                    return Task.FromResult(_snapshotCrawlResult);
                }

                throw new InvalidOperationException("Initial virtual-files population must use streaming remote crawl.");
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                StreamingCrawlCalls++;
                var root = new NodeDto
                {
                    Id = _rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null));
                if (_entriesExpected.HasValue)
                {
                    progress?.Report(new RemoteTreeScanProgress(
                        0,
                        0,
                        currentPath: null,
                        pagesScanned: 1,
                        entriesExpected: _entriesExpected));
                }

                for (int index = 0; index < _files.Count; index++)
                {
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new RemoteTreeScanProgress(
                        index + 1,
                        0,
                        file.RelativePath,
                        pagesScanned: 1,
                        entriesExpected: _entriesExpected));
                    if (index == 0)
                    {
                        try
                        {
                            await FirstPlaceholderStarted.Task
                                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                                .ConfigureAwait(false);
                            FirstPlaceholderStartedBeforeStreamingCompleted = !StreamingCompleted;
                        }
                        catch (TimeoutException)
                        {
                            FirstPlaceholderStartedBeforeStreamingCompleted = false;
                        }
                    }
                }

                StreamingCompleted = true;
                progress?.Report(new RemoteTreeScanProgress(
                    _files.Count,
                    0,
                    currentPath: null,
                    pagesScanned: 1,
                    entriesExpected: _entriesExpected));
                return root;
            }
        }

        private sealed class StreamingRemoteTreeCrawler : IRemoteTreeStreamingCrawler
        {
            private readonly Guid _rootNodeId;
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;
            private readonly IReadOnlyList<RemoteDirectorySnapshot> _directories;

            public StreamingRemoteTreeCrawler(
                Guid rootNodeId,
                IReadOnlyList<RemoteFileSnapshot> files,
                IReadOnlyList<RemoteDirectorySnapshot>? directories = null)
            {
                _rootNodeId = rootNodeId;
                _files = files;
                _directories = directories ?? [];
            }

            public int SnapshotCrawlCalls { get; private set; }

            public int StreamingCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                throw new InvalidOperationException("Initial virtual-files population must use streaming remote crawl.");
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                StreamingCrawlCalls++;
                var root = new NodeDto
                {
                    Id = _rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null));
                for (int index = 0; index < _directories.Count; index++)
                {
                    RemoteDirectorySnapshot directory = _directories[index];
                    await sink.AddDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new RemoteTreeScanProgress(
                        0,
                        index + 1,
                        directory.RelativePath,
                        pagesScanned: 1));
                }

                for (int index = 0; index < _files.Count; index++)
                {
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new RemoteTreeScanProgress(
                        index + 1,
                        _directories.Count,
                        file.RelativePath,
                        pagesScanned: 1));
                }

                progress?.Report(new RemoteTreeScanProgress(
                    _files.Count,
                    _directories.Count,
                    currentPath: null,
                    pagesScanned: 1));
                return root;
            }
        }

        private class LookupOnlyRemoteTreeCrawler : IRemoteTreeLookupCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;

            public LookupOnlyRemoteTreeCrawler(RemoteTreeSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int LookupCrawlCalls { get; private set; }

            public int ProgressCrawlCalls { get; private set; }

            public int SnapshotCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(
                Guid rootNodeId,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                ProgressCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlLookupsAsync(
                Guid rootNodeId,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                LookupCrawlCalls++;
                var snapshot = new RemoteTreeLookupSnapshot
                {
                    RootNode = _snapshot.RootNode,
                };
                foreach (RemoteDirectorySnapshot directory in _snapshot.Directories)
                {
                    snapshot.DirectoriesByPath.Add(SyncPath.ToKey(directory.RelativePath), directory);
                }

                foreach (RemoteFileSnapshot file in _snapshot.Files)
                {
                    snapshot.FilesByPath.Add(SyncPath.ToKey(file.RelativePath), file);
                }

                return Task.FromResult(snapshot);
            }
        }

        private class PathOnlyRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;

            public PathOnlyRemoteTreeCrawler(RemoteTreeSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int FullCrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                FullCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                var snapshot = new RemoteTreeLookupSnapshot
                {
                    RootNode = _snapshot.RootNode,
                };
                foreach (RemoteDirectorySnapshot directory in _snapshot.Directories)
                {
                    if (relativePaths.Contains(directory.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in _snapshot.Files)
                {
                    if (relativePaths.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }
        }

        private class DescendantPathRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;

            public DescendantPathRemoteTreeCrawler(RemoteTreeSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int FullCrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                FullCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                RemoteTreeLookupSnapshot snapshot = new()
                {
                    RootNode = _snapshot.RootNode,
                };
                string[] requestedPaths = relativePaths.Select(SyncPath.Normalize).ToArray();
                foreach (RemoteDirectorySnapshot directory in _snapshot.Directories)
                {
                    if (requestedPaths.Any(path => ContainsRequestedPath(directory.RelativePath, path)))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in _snapshot.Files)
                {
                    if (requestedPaths.Any(path => ContainsRequestedPath(file.RelativePath, path)))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }

            private static bool ContainsRequestedPath(string relativePath, string requestedPath)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                string normalizedRequestedPath = SyncPath.Normalize(requestedPath).TrimEnd('/');
                return normalizedPath.Equals(normalizedRequestedPath, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(normalizedRequestedPath + "/", StringComparison.OrdinalIgnoreCase)
                    || normalizedRequestedPath.StartsWith(normalizedPath + "/", StringComparison.OrdinalIgnoreCase);
            }
        }

        private class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private class RecordingProgress<T> : IProgress<T>
        {
            private readonly Action<T>? _onReport;

            public RecordingProgress(Action<T>? onReport = null)
            {
                _onReport = onReport;
            }

            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
                _onReport?.Invoke(value);
            }
        }

        private class FakeRemoteFilePlaceholderWriter :
            IRemoteFilePlaceholderWriter,
            IRemoteFilePlaceholderPopulationObserver,
            IRemoteFileMaterializationObserver,
            IRemoteDirectoryMaterializationObserver,
            IRemoteDirectoryTreePopulationObserver
        {
            private readonly object _requestsLock = new();

            public byte[] PlaceholderIdentity { get; } = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];

            public List<RemoteFilePlaceholderRequest> Requests { get; } = [];

            public List<RemoteFileMaterializationRequest> FileMaterializationRequests { get; } = [];

            public List<bool> FileExistsWhenMaterializationRequested { get; } = [];

            public List<RemoteDirectoryMaterializationRequest> DirectoryRequests { get; } = [];

            public List<RemoteDirectoryMaterializationRequest> CompletedDirectoryRequests { get; } = [];

            public List<IReadOnlyList<RemoteDirectoryMaterializationRequest>> CompletedDirectoryTreeRequests { get; } = [];

            public List<bool> DirectoryExistsWhenCompleted { get; } = [];

            public List<int> PlaceholderCountWhenDirectoryTreeCompleted { get; } = [];

            public string? UnavailableReason { get; set; }

            public SyncPlaceholderHydrationState HydrationState { get; set; } = SyncPlaceholderHydrationState.RemoteOnly;

            public long? LocalSizeBytes { get; set; }

            public DateTime? LocalLastWriteUtc { get; set; }

            public int BeginPopulationCalls { get; private set; }

            public int EndPopulationCalls { get; private set; }

            public IDisposable BeginPopulation(string syncPairId, string localRootPath)
            {
                BeginPopulationCalls++;
                return new PopulationLease(this);
            }

            public Task BeforeWriteFileAsync(
                RemoteFileMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                FileMaterializationRequests.Add(request);
                FileExistsWhenMaterializationRequested.Add(File.Exists(Path.Combine(
                    request.LocalRootPath,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                return Task.CompletedTask;
            }

            public Task BeforeCreateDirectoryAsync(
                RemoteDirectoryMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                DirectoryRequests.Add(request);
                return Task.CompletedTask;
            }

            public Task AfterCreateDirectoryAsync(
                RemoteDirectoryMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                CompletedDirectoryRequests.Add(request);
                DirectoryExistsWhenCompleted.Add(Directory.Exists(Path.Combine(
                    request.LocalRootPath,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                return Task.CompletedTask;
            }

            public Task AfterDirectoryTreePopulationAsync(
                IReadOnlyList<RemoteDirectoryMaterializationRequest> directories,
                CancellationToken cancellationToken = default)
            {
                CompletedDirectoryTreeRequests.Add(directories.ToArray());
                lock (_requestsLock)
                {
                    PlaceholderCountWhenDirectoryTreeCompleted.Add(Requests.Count);
                }

                return Task.CompletedTask;
            }

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_requestsLock)
                {
                    Requests.Add(request);
                }

                if (!string.IsNullOrWhiteSpace(UnavailableReason))
                {
                    throw new RemoteFilePlaceholderUnavailableException(request.RelativePath, UnavailableReason);
                }

                return Task.FromResult(new RemoteFilePlaceholderResult(
                    PlaceholderIdentity,
                    HydrationState,
                    LocalSizeBytes,
                    LocalLastWriteUtc));
            }

            private sealed class PopulationLease : IDisposable
            {
                private FakeRemoteFilePlaceholderWriter? _owner;

                public PopulationLease(FakeRemoteFilePlaceholderWriter owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    FakeRemoteFilePlaceholderWriter? owner = Interlocked.Exchange(ref _owner, null);
                    if (owner is not null)
                    {
                        owner.EndPopulationCalls++;
                    }
                }
            }
        }

        private sealed class SignalingRemoteFilePlaceholderWriter : IRemoteFilePlaceholderWriter
        {
            private static readonly byte[] PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];
            private readonly object _requestsLock = new();
            private readonly TaskCompletionSource _firstPlaceholderStarted;

            private readonly SyncPlaceholderHydrationState _hydrationState;

            public SignalingRemoteFilePlaceholderWriter(
                TaskCompletionSource firstPlaceholderStarted,
                SyncPlaceholderHydrationState hydrationState = SyncPlaceholderHydrationState.RemoteOnly)
            {
                _firstPlaceholderStarted = firstPlaceholderStarted;
                _hydrationState = hydrationState;
            }

            public List<RemoteFilePlaceholderRequest> Requests { get; } = [];

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_requestsLock)
                {
                    Requests.Add(request);
                }

                _firstPlaceholderStarted.TrySetResult();
                return Task.FromResult(new RemoteFilePlaceholderResult(PlaceholderIdentity, _hydrationState));
            }
        }

        private sealed class BatchRemoteFilePlaceholderWriter : IRemoteFilePlaceholderBatchWriter
        {
            private static readonly byte[] PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];

            public List<string> SingleRequests { get; } = [];

            public List<IReadOnlyList<string>> Batches { get; } = [];

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                SingleRequests.Add(request.RelativePath);
                return Task.FromResult(new RemoteFilePlaceholderResult(PlaceholderIdentity));
            }

            public Task<IReadOnlyList<RemoteFilePlaceholderBatchResult>> CreatePlaceholdersAsync(
                IReadOnlyList<RemoteFilePlaceholderRequest> requests,
                CancellationToken cancellationToken = default)
            {
                Batches.Add(requests.Select(static request => request.RelativePath).ToArray());
                return Task.FromResult<IReadOnlyList<RemoteFilePlaceholderBatchResult>>(
                    requests
                        .Select(static request => RemoteFilePlaceholderBatchResult.Success(
                            request,
                            new RemoteFilePlaceholderResult(PlaceholderIdentity)))
                        .ToArray());
            }
        }

        private class FakeRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            public List<UploadCall> Uploads { get; } = [];

            public List<MoveCall> Moves { get; } = [];

            public List<string> UploadInputContentHashes { get; } = [];

            public List<Guid> DownloadCalls { get; } = [];

            public List<(Guid NodeFileId, bool SkipTrash, string? ExpectedETag)> Deletes { get; } = [];

            public Dictionary<Guid, byte[]> Downloads { get; } = [];

            public HashSet<Guid> UploadFailureIds { get; } = [];

            public HashSet<string> UploadFailureRelativePaths { get; } = [];

            public HashSet<string> CreateConflictRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<Guid> DownloadFailureIds { get; } = [];

            public HashSet<Guid> PartialDownloadFailureIds { get; } = [];

            public HashSet<Guid> DeleteFailureIds { get; } = [];

            public HashSet<Guid> PreconditionFailedUploadIds { get; } = [];

            public HashSet<Guid> PreconditionFailedDeleteIds { get; } = [];

            public HashSet<Guid> PreconditionFailedMoveIds { get; } = [];

            public HashSet<string> LocalUnavailableUploadRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public string? EmptyLocalHashUploadContentHash { get; set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                if (existingRemoteFile is null && CreateConflictRelativePaths.Contains(relativePath))
                {
                    throw new HttpRequestException(
                        "Remote file already exists.",
                        inner: null,
                        HttpStatusCode.Conflict);
                }

                if (existingRemoteFile is not null && PreconditionFailedUploadIds.Contains(existingRemoteFile.Id))
                {
                    throw new HttpRequestException(
                        "Remote file changed before upload.",
                        inner: null,
                        HttpStatusCode.PreconditionFailed);
                }

                if (existingRemoteFile is not null && UploadFailureIds.Contains(existingRemoteFile.Id))
                {
                    throw new InvalidOperationException("Remote upload failed.");
                }

                if (UploadFailureRelativePaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException(
                        "Remote upload failed.",
                        inner: null,
                        HttpStatusCode.ServiceUnavailable);
                }

                if (LocalUnavailableUploadRelativePaths.Contains(relativePath))
                {
                    throw new LocalFileUnavailableException(
                        relativePath,
                        localFile.FullPath,
                        "the file changed during upload.");
                }

                UploadInputContentHashes.Add(localFile.ContentHash);
                string uploadedContentHash = string.IsNullOrWhiteSpace(localFile.ContentHash)
                    ? EmptyLocalHashUploadContentHash ?? localFile.ContentHash
                    : localFile.ContentHash;
                var returned = new NodeFileManifestDto
                {
                    Id = existingRemoteFile?.Id ?? Guid.NewGuid(),
                    NodeId = existingRemoteFile?.NodeId ?? rootNodeId,
                    FileManifestId = Guid.NewGuid(),
                    OriginalNodeFileId = existingRemoteFile?.OriginalNodeFileId == Guid.Empty
                        ? Guid.NewGuid()
                        : existingRemoteFile?.OriginalNodeFileId ?? Guid.NewGuid(),
                    OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Name = relativePath.Split('/')[^1],
                    ContentType = "application/octet-stream",
                    SizeBytes = localFile.SizeBytes,
                    ContentHash = uploadedContentHash,
                    ETag = "sha256-" + uploadedContentHash,
                    CreatedAt = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath.Replace('\\', '/') },
                };
                Uploads.Add(new UploadCall(rootNodeId, relativePath, localFile, existingRemoteFile, returned));
                return Task.FromResult(returned);
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                if (PreconditionFailedMoveIds.Contains(existingRemoteFile.Id))
                {
                    throw new HttpRequestException(
                        "Remote file changed before move.",
                        inner: null,
                        HttpStatusCode.PreconditionFailed);
                }

                string normalizedPath = relativePath.Replace('\\', '/');
                NodeFileManifestDto moved = new()
                {
                    Id = existingRemoteFile.Id,
                    NodeId = rootNodeId,
                    FileManifestId = existingRemoteFile.FileManifestId,
                    OriginalNodeFileId = existingRemoteFile.OriginalNodeFileId,
                    OwnerId = existingRemoteFile.OwnerId,
                    Name = normalizedPath.Split('/')[^1],
                    ContentType = existingRemoteFile.ContentType,
                    SizeBytes = existingRemoteFile.SizeBytes,
                    ContentHash = existingRemoteFile.ContentHash,
                    ETag = existingRemoteFile.ETag,
                    CreatedAt = existingRemoteFile.CreatedAt,
                    UpdatedAt = new DateTime(2026, 6, 2, 14, 0, 0, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string> { ["relativePath"] = normalizedPath },
                };
                Moves.Add(new MoveCall(rootNodeId, normalizedPath, existingRemoteFile, moved));
                return Task.FromResult(moved);
            }

            public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                DownloadCalls.Add(nodeFileId);
                if (DownloadFailureIds.Contains(nodeFileId))
                {
                    throw new InvalidOperationException("Remote download failed.");
                }

                byte[] bytes = Downloads[nodeFileId];
                if (PartialDownloadFailureIds.Contains(nodeFileId))
                {
                    int partialLength = Math.Max(1, bytes.Length / 2);
                    destination.Write(bytes, 0, partialLength);
                    throw new CottonApiException(
                        HttpStatusCode.ServiceUnavailable,
                        "{\"message\":\"Download interrupted.\"}",
                        "Cotton API download failed with status 503 (ServiceUnavailable).");
                }

                return destination.WriteAsync(bytes, cancellationToken).AsTask();
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                Deletes.Add((nodeFileId, skipTrash, expectedETag));
                if (DeleteFailureIds.Contains(nodeFileId))
                {
                    throw new InvalidOperationException("Remote delete failed.");
                }

                if (PreconditionFailedDeleteIds.Contains(nodeFileId))
                {
                    throw new HttpRequestException(
                        "Remote file changed before delete.",
                        inner: null,
                        HttpStatusCode.PreconditionFailed);
                }

                return Task.CompletedTask;
            }
        }

        private class FakeRemoteDirectorySynchronizer : IRemoteDirectorySynchronizer
        {
            public List<CreateDirectoryCall> CreateAttempts { get; } = [];

            public List<CreateDirectoryCall> Creates { get; } = [];

            public List<(Guid NodeId, bool SkipTrash)> Deletes { get; } = [];

            public List<(Guid ParentNodeId, string Name)> ConflictCreates { get; } = [];

            public List<NodeDto> ExistingDirectories { get; } = [];

            public List<(Guid ParentNodeId, string Name)> FindChildDirectoryCalls { get; } = [];

            public Task<NodeDto?> FindChildDirectoryAsync(
                Guid parentNodeId,
                string name,
                CancellationToken cancellationToken = default)
            {
                FindChildDirectoryCalls.Add((parentNodeId, name));
                NodeDto? match = ExistingDirectories.FirstOrDefault(node =>
                    node.ParentId == parentNodeId
                    && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(match);
            }

            public Task<NodeDto> CreateDirectoryAsync(
                Guid parentNodeId,
                string name,
                CancellationToken cancellationToken = default)
            {
                CreateAttempts.Add(new CreateDirectoryCall(parentNodeId, name, new NodeDto()));
                if (ConflictCreates.Any(item =>
                    item.ParentNodeId == parentNodeId
                    && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new CottonApiException(
                        HttpStatusCode.Conflict,
                        "{\"message\":\"A folder with the same name already exists.\"}",
                        "Cotton API request PUT /api/v1/layouts/nodes failed with status 409 (Conflict).");
                }

                NodeDto node = new()
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentNodeId,
                    Name = name,
                };
                Creates.Add(new CreateDirectoryCall(parentNodeId, name, node));
                return Task.FromResult(node);
            }

            public Task DeleteDirectoryAsync(Guid nodeId, bool skipTrash = false, CancellationToken cancellationToken = default)
            {
                Deletes.Add((nodeId, skipTrash));
                return Task.CompletedTask;
            }
        }

        private record CreateDirectoryCall(Guid ParentNodeId, string Name, NodeDto ReturnedNode);

        private record UploadCall(
            Guid RootNodeId,
            string RelativePath,
            LocalFileSnapshot LocalFile,
            NodeFileManifestDto? ExistingRemoteFile,
            NodeFileManifestDto ReturnedFile);

        private record MoveCall(
            Guid RootNodeId,
            string RelativePath,
            NodeFileManifestDto ExistingRemoteFile,
            NodeFileManifestDto ReturnedFile);

        private abstract class DelegatingStateStore : ISyncStateStore
        {
            private readonly ISyncStateStore _inner;

            protected DelegatingStateStore(ISyncStateStore inner)
            {
                _inner = inner;
            }

            public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return _inner.InitializeAsync(cancellationToken);
            }

            public virtual Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.LoadPairAsync(syncPairId, cancellationToken);
            }

            public virtual IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return _inner.LoadPairEntriesAsync(syncPairId, cancellationToken);
            }

            public virtual IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
                string syncPairId,
                IEnumerable<string> relativePathKeys,
                CancellationToken cancellationToken = default)
            {
                return _inner.LoadEntriesByPathKeysAsync(syncPairId, relativePathKeys, cancellationToken);
            }

            public virtual Task<DateTime?> GetPairLastSyncedAtUtcAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.GetPairLastSyncedAtUtcAsync(syncPairId, cancellationToken);
            }

            public virtual Task<SyncChangeCursor> GetChangeCursorAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.GetChangeCursorAsync(syncPairId, cancellationToken);
            }

            public virtual Task<SyncStateEntry?> GetAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                return _inner.GetAsync(syncPairId, relativePath, cancellationToken);
            }

            public virtual Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                return _inner.UpsertAsync(entry, cancellationToken);
            }

            public virtual Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return _inner.SaveChangeCursorAsync(cursor, cancellationToken);
            }

            public virtual Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                return _inner.DeleteAsync(syncPairId, relativePath, cancellationToken);
            }

            public virtual Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.DeletePairAsync(syncPairId, cancellationToken);
            }

            public virtual Task ReplacePairAsync(string syncPairId, IReadOnlyCollection<SyncStateEntry> entries, CancellationToken cancellationToken = default)
            {
                return _inner.ReplacePairAsync(syncPairId, entries, cancellationToken);
            }
        }

        private class FailingUpsertStateStore : DelegatingStateStore
        {
            public FailingUpsertStateStore(ISyncStateStore inner)
                : base(inner)
            {
            }

            public override Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("State write failed.");
            }
        }

        private class FailingDeleteStateStore : DelegatingStateStore
        {
            public FailingDeleteStateStore(ISyncStateStore inner)
                : base(inner)
            {
            }

            public override Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("State delete failed.");
            }
        }

        private class StreamingOnlyStateStore : DelegatingStateStore
        {
            public StreamingOnlyStateStore(ISyncStateStore inner)
                : base(inner)
            {
            }

            public int LoadPairEntriesCallCount { get; private set; }

            public int LoadEntriesByPathKeysCallCount { get; private set; }

            public int GetAsyncCallCount { get; private set; }

            public override Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("SyncEngine should use streamed state loading.");
            }

            public override IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCallCount++;
                return base.LoadPairEntriesAsync(syncPairId, cancellationToken);
            }

            public override IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
                string syncPairId,
                IEnumerable<string> relativePathKeys,
                CancellationToken cancellationToken = default)
            {
                LoadEntriesByPathKeysCallCount++;
                return base.LoadEntriesByPathKeysAsync(syncPairId, relativePathKeys, cancellationToken);
            }

            public override Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                GetAsyncCallCount++;
                return base.GetAsync(syncPairId, relativePath, cancellationToken);
            }
        }
    }
}
