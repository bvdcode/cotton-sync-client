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
        public async Task RunOnceAsync_UploadsUnicodeNamedLocalFileAndStoresBaseline()
        {
            const string relativePath = "Документы/設計-notes.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "unicode-local-content");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
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
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore durableStore = new SqliteSyncStateStore(_databasePath);
            FailingUpsertStateStore failingStore = new FailingUpsertStateStore(durableStore);
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore desktopStateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine desktopRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                desktopStateStore);

            SyncRunResult firstResult = await desktopRun.RunOnceAsync(Pair());

            NodeFileManifestDto uploaded = remoteFiles.Uploads.Single().ReturnedFile;
            SqliteSyncStateStore cliStateStore = new SqliteSyncStateStore(_databasePath);
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
            SqliteSyncStateStore restartedDesktopStateStore = new SqliteSyncStateStore(_databasePath);
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore durableStore = new SqliteSyncStateStore(_databasePath);
            FailingUpsertStateStore cliCrashStore = new FailingUpsertStateStore(durableStore);
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
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SqliteSyncStateStore durableStore = new SqliteSyncStateStore(_databasePath);
            FailingUpsertStateStore desktopCrashStore = new FailingUpsertStateStore(durableStore);
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
    }
}
