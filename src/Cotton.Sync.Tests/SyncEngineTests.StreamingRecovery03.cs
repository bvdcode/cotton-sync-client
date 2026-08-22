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
            StreamingRemoteTreeCrawler remoteCrawler = new StreamingRemoteTreeCrawler(
                _remoteRootNodeId,
                remoteTree.Files,
                remoteTree.Directories);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
            SyncEngine engine = new SyncEngine(
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
            FakeRemoteFileSynchronizer remoteFileSynchronizer = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            List<int> cooperativeYieldRequestCounts = new List<int>();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter
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
    }
}
