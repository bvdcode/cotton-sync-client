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
        public void RunOnceAsync_HonorsCancellationBeforeScanning()
        {
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(LocalFile("cancel.txt", "cancel"));
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out _);
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunOnceAsync(Pair(), cancellationToken: cancellation.Token));
            Assert.That(scanner.ScanCalls, Is.Zero);
        }


        [Test]
        public void RunOnceAsync_RejectsLocalCaseInsensitivePathCollision()
        {
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(LocalFile("Project", "file"));
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await stateStore.InitializeAsync();
            Guid pairBRemoteFileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
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
    }
}
