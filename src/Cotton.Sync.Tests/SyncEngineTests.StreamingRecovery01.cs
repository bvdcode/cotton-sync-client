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
        public async Task RunOnceAsync_WithInitialWindowsVirtualFilesFallsBackToReconcileWhenLocalFilesExist()
        {
            LocalFileSnapshot local = LocalFile("local.txt", "local-content");
            NodeFileManifestDto remote = RemoteFile("remote-only.txt", HashText("remote-content"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            RemoteTreeSnapshot remoteTree = RemoteTree(remote);
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = "remote-only.txt", File = remote }],
                snapshotCrawlResult: remoteTree);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [],
                snapshotCrawlResult: remoteTree);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner();
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [],
                snapshotCrawlResult: EmptyRemoteTree());
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = privateRelativePath,
                Kind = SyncEntryKind.File,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
                PlaceholderIdentity = [1, 2, 3],
            });
            RecordingLogger<SyncEngine> logger = new RecordingLogger<SyncEngine>();
            SyncEngine engine = new SyncEngine(
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
    }
}
