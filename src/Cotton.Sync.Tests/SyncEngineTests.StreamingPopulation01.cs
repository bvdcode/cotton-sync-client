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
        public async Task RunOnceAsync_DownloadsRemoteOnlyFileAndStoresBaseline()
        {
            byte[] content = Encoding.UTF8.GetBytes("remote-content");
            NodeFileManifestDto remote = RemoteFile("remote.txt", Hash(content), sizeBytes: content.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            RecordingProgress<SyncTransferProgress> transferProgress = new RecordingProgress<SyncTransferProgress>();
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
        public void RunOnceAsync_WithWindowsVirtualFilesStreamingRejectsCaseInsensitivePathCollision()
        {
            RemoteFileSnapshot[] remoteFiles =
            [
                new() { RelativePath = "Case.txt", File = RemoteFile("Case.txt", HashText("first"), sizeBytes: 5) },
                new() { RelativePath = "case.txt", File = RemoteFile("case.txt", HashText("second"), sizeBytes: 6) },
            ];
            StreamingRemoteTreeCrawler remoteCrawler = new(_remoteRootNodeId, remoteFiles);
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                remoteCrawler,
                new FakeRemoteFileSynchronizer(),
                new SqliteSyncStateStore(_databasePath),
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncPathCollisionException? exception = Assert.ThrowsAsync<SyncPathCollisionException>(
                () => engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles)));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.FirstPath, Is.EqualTo("Case.txt"));
                Assert.That(exception?.SecondPath, Is.EqualTo("case.txt"));
                Assert.That(remoteCrawler.StreamingCrawlCalls, Is.EqualTo(1));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesStreamingKeepsAccentedNamesDistinct()
        {
            RemoteFileSnapshot[] remoteFiles =
            [
                new() { RelativePath = "Cafe.txt", File = RemoteFile("Cafe.txt", HashText("plain"), sizeBytes: 5) },
                new() { RelativePath = "Café.txt", File = RemoteFile("Café.txt", HashText("accented"), sizeBytes: 8) },
            ];
            StreamingRemoteTreeCrawler remoteCrawler = new(_remoteRootNodeId, remoteFiles);
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                remoteCrawler,
                new FakeRemoteFileSynchronizer(),
                new SqliteSyncStateStore(_databasePath),
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    placeholderWriter.Requests.Select(static request => request.RelativePath),
                    Is.EquivalentTo(new[] { "Cafe.txt", "Café.txt" }));
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
            BlockingStreamingRemoteTreeCrawler remoteCrawler = new BlockingStreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            SignalingRemoteFilePlaceholderWriter placeholderWriter = new SignalingRemoteFilePlaceholderWriter(remoteCrawler.FirstPlaceholderStarted);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            RecordingLogger<SyncEngine> logger = new RecordingLogger<SyncEngine>();
            SyncEngine engine = new SyncEngine(
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
            StreamingRemoteTreeCrawler remoteCrawler = new StreamingRemoteTreeCrawler(_remoteRootNodeId, remoteFiles);
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            BatchRemoteFilePlaceholderWriter placeholderWriter = new BatchRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(
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
    }
}
