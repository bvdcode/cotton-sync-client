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
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, "Desktop/existing.txt", existingRemote);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal("Desktop/existing.txt", existingRemote.SizeBytes));
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
        public async Task RunOnceAsync_WithWindowsVirtualFilesRecreatesPlaceholderMissingAfterResumeInspection()
        {
            string relativePath = "Desktop/disappeared.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 11);
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = remote }]);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);
            FakeLocalFileScanner scanner = new(CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes))
            {
                FileExistsFactory = _ => false,
            };
            SyncEngine engine = new(
                scanner,
                remoteCrawler,
                remoteFileSynchronizer,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { InitialVirtualFilesPopulationQueueCapacity = 1 });

            Assert.Multiple(() =>
            {
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
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
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore innerStateStore = new SqliteSyncStateStore(_databasePath);
            await InsertPlaceholderBaselineAsync(innerStateStore, "Desktop/existing.txt", existingRemote);
            StreamingOnlyStateStore stateStore = new StreamingOnlyStateStore(innerStateStore);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal("Desktop/existing.txt", existingRemote.SizeBytes));
            SyncEngine engine = new SyncEngine(
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
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                [new RemoteFileSnapshot { RelativePath = relativePath, File = newRemote }]);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, oldRemote);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(CloudFilesPlaceholderLocal(relativePath, oldRemote.SizeBytes));
            SyncEngine engine = new SyncEngine(
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
    }
}
