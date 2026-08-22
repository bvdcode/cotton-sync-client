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
        public async Task RunOnceAsync_MovesRemoteFileWhenLocalPathChangesWithoutContentChange()
        {
            string oldPath = "Project/old-name.txt";
            string newPath = "Project/new-name.txt";
            string content = "same-content";
            WriteFile(newPath, content);
            LocalFileSnapshot local = LocalFile(newPath, content);
            NodeFileManifestDto oldRemote = RemoteFile(oldPath, local.ContentHash, sizeBytes: local.SizeBytes);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            LocalFileSnapshot local = new LocalFileSnapshot
            {
                RelativePath = "Docs/existing.bin",
                FullPath = Path.Combine(_root, "Docs", "existing.bin"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 6, 8, 0, 0, DateTimeKind.Utc),
            };
            MetadataOnlyLocalFileScanner scanner = new MetadataOnlyLocalFileScanner(local);
            NodeFileManifestDto remote = RemoteFile("Docs/existing.bin", baselineHash, sizeBytes: local.SizeBytes);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            DateTime baselineSyncedAtUtc = new DateTime(2026, 6, 6, 8, 1, 0, DateTimeKind.Utc);
            LocalFileSnapshot local = new LocalFileSnapshot
            {
                RelativePath = "Docs/existing.bin",
                FullPath = Path.Combine(_root, "Docs", "existing.bin"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 6, 8, 0, 0, DateTimeKind.Utc),
            };
            MetadataOnlyLocalFileScanner scanner = new MetadataOnlyLocalFileScanner(local);
            NodeFileManifestDto remote = RemoteFile("Docs/existing.bin", baselineHash, sizeBytes: local.SizeBytes);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(
                LocalFile("Docs/a.txt", "a"),
                LocalFile("Docs/b.txt", "b"));
            RecordingProgress<SyncRunProgress> progress = new RecordingProgress<SyncRunProgress>();
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
        public async Task RunOnceAsync_WithMixedWindowsVirtualFilesProgressCountsOnlyPlaceholders()
        {
            FakeLocalFileScanner scanner = new(
                LocalFile("Docs/local-a.txt", "a"),
                LocalFile("Docs/local-b.txt", "b"));
            NodeFileManifestDto remoteOnly = RemoteFile(
                "Docs/remote-only.txt",
                HashText("remote"),
                sizeBytes: 6);
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            RecordingProgress<SyncRunProgress> progress = new();
            SyncEngine engine = CreateEngine(
                scanner,
                RemoteTree(remoteOnly),
                new FakeRemoteFileSynchronizer(),
                out _,
                remoteFilePlaceholderWriter: placeholderWriter);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    AllowInitialVirtualFilesStreaming = false,
                    RunProgress = progress,
                });

            IReadOnlyList<SyncRunProgress> placeholderProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.CreatingPlaceholders)
                .ToList();
            IReadOnlyList<SyncRunProgress> regularFileProgress = progress.Values
                .Where(item => item.Stage == SyncRunProgressStage.ReconcilingFiles)
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(placeholderProgress.Select(item => item.FilesTotal).Distinct(), Is.EqualTo(new int?[] { 1 }));
                Assert.That(placeholderProgress.Select(item => item.FilesCompleted).Distinct(), Is.EqualTo(new[] { 0, 1 }));
                Assert.That(regularFileProgress.Select(item => item.FilesTotal).Distinct(), Is.EqualTo(new int?[] { 2 }));
                Assert.That(regularFileProgress.Select(item => item.FilesCompleted).Distinct(), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(
                    placeholderWriter.Requests.Select(request => request.RelativePath),
                    Is.EqualTo(new[] { "Docs/remote-only.txt" }));
            });
        }


        [Test]
        public async Task RunOnceAsync_ReportsLocalScanFileDiscoveryProgress()
        {
            MetadataOnlyLocalFileScanner scanner = new MetadataOnlyLocalFileScanner(
                LocalFile("Docs/a.txt", "a"),
                LocalFile("Docs/b.txt", "b"))
            {
                ReportMetadataScanProgress = true,
            };
            RecordingProgress<SyncRunProgress> progress = new RecordingProgress<SyncRunProgress>();
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
    }
}
