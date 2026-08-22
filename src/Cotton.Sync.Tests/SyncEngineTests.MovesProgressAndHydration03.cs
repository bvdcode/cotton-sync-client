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
        public async Task RunOnceAsync_KeepsPlannedByteProgressStableWhenLazyHashCreatesConflict()
        {
            const string relativePath = "Docs/conflict.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-content");
            LocalFileSnapshot local = new LocalFileSnapshot
            {
                RelativePath = relativePath,
                FullPath = Path.Combine(_root, "Docs", "conflict.txt"),
                ContentHash = string.Empty,
                SizeBytes = 1024,
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
            };
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            MetadataOnlyLocalFileScanner scanner = new MetadataOnlyLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            RecordingProgress<SyncRunProgress> runProgress = new RecordingProgress<SyncRunProgress>();
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
            MetadataOnlyLocalFileScanner scanner = new MetadataOnlyLocalFileScanner(local);
            RecordingProgress<SyncTransferProgress> transferProgress = new RecordingProgress<SyncTransferProgress>();
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
                Assert.That(materializationObserver.CompletedFileMaterializationRequests, Has.Count.EqualTo(1));
                Assert.That(materializationObserver.FileExistsWhenMaterializationCompleted, Is.EqualTo(new[] { true }));
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
        public async Task RunOnceAsync_WithWindowsVirtualFilesSuppressesWatcherBeforeRestoringRemoteEditAfterLocalDelete()
        {
            const string relativePath = "Docs/restored.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-changed");
            string oldHash = HashText("old-content");
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(
                relativePath,
                oldHash,
                remoteFileId,
                sizeBytes: Encoding.UTF8.GetByteCount("old-content"));
            NodeFileManifestDto changedRemote = RemoteFile(
                relativePath,
                Hash(remoteContent),
                remoteFileId,
                sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[changedRemote.Id] = remoteContent;
            FakeRemoteFilePlaceholderWriter materializationObserver = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
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
                LocalSizeBytes = baselineRemote.SizeBytes,
                RemoteNodeId = baselineRemote.NodeId,
                RemoteFileId = baselineRemote.Id,
                RemoteSizeBytes = baselineRemote.SizeBytes,
                RemoteContentHash = baselineRemote.ContentHash,
                RemoteETag = baselineRemote.ETag,
                PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated,
            });

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(File.ReadAllText(Path.Combine(_root, "Docs", "restored.txt")), Is.EqualTo("remote-changed"));
                Assert.That(materializationObserver.FileMaterializationRequests, Has.Count.EqualTo(1));
                Assert.That(materializationObserver.FileMaterializationRequests[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(materializationObserver.FileExistsWhenMaterializationRequested, Is.EqualTo(new[] { false }));
                Assert.That(materializationObserver.CompletedFileMaterializationRequests, Is.Empty);
            });
        }
    }
}
