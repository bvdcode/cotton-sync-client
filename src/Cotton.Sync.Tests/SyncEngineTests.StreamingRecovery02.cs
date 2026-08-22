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
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesCurrentUntrackedCloudFilesPlaceholderIdentity()
        {
            string relativePath = "Desktop/orphaned-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 12);
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }]);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            SyncEngine engine = new SyncEngine(
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
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }]);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt.AddMinutes(-5);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            SyncEngine engine = new SyncEngine(
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
            StreamingRemoteTreeCrawler remoteCrawler = new StreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }],
                [remoteDirectory]);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes));
            scanner.Directories.Add(LocalDirectory("Desktop"));
            SyncEngine engine = new SyncEngine(
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
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                remoteFiles,
                entriesExpected: remoteFiles.Count);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = new SyncEngine(
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
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
    }
}
