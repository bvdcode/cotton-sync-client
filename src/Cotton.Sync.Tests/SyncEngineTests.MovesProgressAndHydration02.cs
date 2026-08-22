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
        public async Task RunOnceAsync_ReportsRemoteScanFileDiscoveryProgress()
        {
            RecordingProgress<SyncRunProgress> progress = new RecordingProgress<SyncRunProgress>();
            FakeRemoteTreeProgressCrawler remoteCrawler = new FakeRemoteTreeProgressCrawler(
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            RecordingProgress<SyncRunProgress> progress = new RecordingProgress<SyncRunProgress>();
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
            List<LocalFileSnapshot> locals = new List<LocalFileSnapshot>();
            List<NodeFileManifestDto> remotes = new List<NodeFileManifestDto>();
            for (int index = 0; index < fileCount; index++)
            {
                string path = "Docs/file-" + index.ToString("000", CultureInfo.InvariantCulture) + ".txt";
                string content = "content-" + index.ToString(CultureInfo.InvariantCulture);
                LocalFileSnapshot local = LocalFile(path, content);
                NodeFileManifestDto remote = RemoteFile(path, local.ContentHash, sizeBytes: local.SizeBytes);
                locals.Add(local);
                remotes.Add(remote);
            }

            RecordingProgress<SyncRunProgress> progress = new RecordingProgress<SyncRunProgress>();
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner();
            for (int index = 0; index < directoryCount; index++)
            {
                scanner.Directories.Add(LocalDirectory("Folder-" + index.ToString("000", CultureInfo.InvariantCulture)));
            }

            RecordingProgress<SyncRunProgress> progress = new RecordingProgress<SyncRunProgress>();
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
            List<string> eventLog = new List<string>();
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>(
                item => eventLog.Add($"run:{item.Stage}:{item.FilesCompleted}:{item.CurrentPath}:{item.IsCompleted}"));
            RecordingProgress<SyncTransferProgress> transferProgress = new RecordingProgress<SyncTransferProgress>(
                item => eventLog.Add($"transfer:{item.Direction}:{item.RelativePath}:{item.TransferredBytes}:{item.TotalBytes}:{item.IsCompleted}"));
            RecordingProgress<SyncActivity> activityProgress = new RecordingProgress<SyncActivity>(
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
    }
}
