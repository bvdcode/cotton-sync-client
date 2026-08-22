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
        public async Task RunOnceAsync_RecoversAfterTransientUploadFailureWithoutStaleBaseline()
        {
            string relativePath = "network-drop-upload.txt";
            LocalFileSnapshot local = LocalFile(relativePath, "local");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.UploadFailureRelativePaths.Add(relativePath);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            SyncStateEntry? failedEntry = await stateStore.GetAsync("pair-a", relativePath);
            remoteFiles.UploadFailureRelativePaths.Clear();
            SyncEngine secondRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? recoveredEntry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(failedEntry, Is.Null);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(recoveredEntry, Is.Not.Null);
                Assert.That(recoveredEntry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(recoveredEntry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_SkipsChangedLocalFileDuringUploadAndContinuesPass()
        {
            LocalFileSnapshot volatileLocal = LocalFile("hot/volatile.txt", "first local content");
            LocalFileSnapshot stableLocal = LocalFile("hot/stable.txt", "stable local content");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(volatileLocal, stableLocal);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.LocalUnavailableUploadRelativePaths.Add(volatileLocal.RelativePath);
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? volatileEntry = await stateStore.GetAsync("pair-a", volatileLocal.RelativePath);
            SyncStateEntry? stableEntry = await stateStore.GetAsync("pair-a", stableLocal.RelativePath);
            SyncActivity volatileActivity = result.Activities.Single(activity => activity.RelativePath == volatileLocal.RelativePath);
            SyncActivity stableActivity = result.Activities.Single(activity => activity.RelativePath == stableLocal.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Has.Count.EqualTo(2));
                Assert.That(volatileActivity.Kind, Is.EqualTo(SyncActivityKind.Skipped));
                Assert.That(volatileActivity.RequiresUserAction, Is.False);
                Assert.That(volatileActivity.Details, Does.Contain("changed during upload"));
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { volatileLocal.RelativePath }));
                Assert.That(stableActivity.Kind, Is.EqualTo(SyncActivityKind.Uploaded));
                Assert.That(remoteFiles.Uploads.Select(static upload => upload.RelativePath), Is.EqualTo(new[] { stableLocal.RelativePath }));
                Assert.That(volatileEntry, Is.Null);
                Assert.That(stableEntry, Is.Not.Null);
                Assert.That(stableEntry!.LocalContentHash, Is.EqualTo(stableLocal.ContentHash));
                Assert.That(stableEntry.RemoteContentHash, Is.EqualTo(stableLocal.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_DefersFreshLocalUploadUntilQuietWindow()
        {
            LocalFileSnapshot freshLocal = LocalFile("hot/fresh.txt", "fresh local content");
            freshLocal.LastWriteUtc = DateTime.UtcNow;
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(freshLocal);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), remoteFiles, out SqliteSyncStateStore stateStore);
            SyncRunOptions options = new SyncRunOptions { MinimumLocalUploadAge = TimeSpan.FromMinutes(5) };

            SyncRunResult firstResult = await engine.RunOnceAsync(Pair(), options);
            SyncStateEntry? deferredEntry = await stateStore.GetAsync("pair-a", freshLocal.RelativePath);
            freshLocal.LastWriteUtc = DateTime.UtcNow.AddMinutes(-10);
            SyncRunResult secondResult = await engine.RunOnceAsync(Pair(), options);

            SyncStateEntry? uploadedEntry = await stateStore.GetAsync("pair-a", freshLocal.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(firstResult.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped }));
                Assert.That(firstResult.DeferredLocalPaths, Is.EqualTo(new[] { freshLocal.RelativePath }));
                Assert.That(firstResult.Activities.Single().Details, Does.Contain("quiet window"));
                Assert.That(deferredEntry, Is.Null);
                Assert.That(secondResult.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(secondResult.HasDeferredLocalPaths, Is.False);
                Assert.That(remoteFiles.Uploads.Select(static upload => upload.RelativePath), Is.EqualTo(new[] { freshLocal.RelativePath }));
                Assert.That(uploadedEntry, Is.Not.Null);
                Assert.That(uploadedEntry!.LocalContentHash, Is.EqualTo(freshLocal.ContentHash));
                Assert.That(uploadedEntry.RemoteContentHash, Is.EqualTo(freshLocal.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_UploadsAccumulatedLocalChangesAfterTransientUploadFailure()
        {
            LocalFileSnapshot first = LocalFile("offline/first.txt", "first offline local content");
            LocalFileSnapshot second = LocalFile("offline/second.txt", "second offline local content");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(first, second);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.UploadFailureRelativePaths.Add(first.RelativePath);
            remoteFiles.UploadFailureRelativePaths.Add(second.RelativePath);
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            remoteFiles.UploadFailureRelativePaths.Clear();
            SyncEngine secondRun = new(scanner, new FakeRemoteTreeCrawler(EmptyRemoteTree()), remoteFiles, stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());
            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", first.RelativePath);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", second.RelativePath);

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Uploaded }));
                Assert.That(remoteFiles.Uploads.Select(static upload => upload.RelativePath), Is.EqualTo(new[] { first.RelativePath, second.RelativePath }));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(firstEntry!.LocalContentHash, Is.EqualTo(first.ContentHash));
                Assert.That(firstEntry.RemoteContentHash, Is.EqualTo(first.ContentHash));
                Assert.That(secondEntry, Is.Not.Null);
                Assert.That(secondEntry!.LocalContentHash, Is.EqualTo(second.ContentHash));
                Assert.That(secondEntry.RemoteContentHash, Is.EqualTo(second.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_DownloadsRemoteChangeWhenLocalBaselineIsUnchanged()
        {
            string relativePath = "changed-down.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_DoesNotUpdateBaselineWhenRemoteDownloadFails()
        {
            string relativePath = "download-fails.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            NodeFileManifestDto remote = RemoteFile(
                relativePath,
                HashText("remote-new"),
                sizeBytes: Encoding.UTF8.GetByteCount("remote-new"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.DownloadFailureIds.Add(remote.Id);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("old"));
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_RecoversAfterTransientDownloadFailureWithoutStalePartial()
        {
            string relativePath = "network-drop-download.txt";
            WriteFile(relativePath, "local-before-server-error");
            LocalFileSnapshot local = LocalFile(relativePath, "local-before-server-error");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PartialDownloadFailureIds.Add(remote.Id);
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(
                stateStore,
                relativePath,
                local.ContentHash,
                RemoteFile(relativePath, local.ContentHash, remote.Id));
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                stateStore);

            Assert.ThrowsAsync<CottonApiException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            string localPath = Path.Combine(_root, relativePath);
            SyncStateEntry? failedEntry = await stateStore.GetAsync("pair-a", relativePath);
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(localPath), Is.EqualTo("local-before-server-error"));
                Assert.That(failedEntry, Is.Not.Null);
                Assert.That(failedEntry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(failedEntry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
            });

            remoteFiles.PartialDownloadFailureIds.Clear();
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? recoveredEntry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
                Assert.That(File.ReadAllText(localPath), Is.EqualTo("remote"));
                Assert.That(recoveredEntry, Is.Not.Null);
                Assert.That(recoveredEntry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(recoveredEntry.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(recoveredEntry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_DownloadsAccumulatedRemoteChangesAfterTransientDownloadFailure()
        {
            byte[] firstContent = Encoding.UTF8.GetBytes("first remote content");
            byte[] secondContent = Encoding.UTF8.GetBytes("second remote content");
            NodeFileManifestDto first = RemoteFile("offline/remote-first.txt", Hash(firstContent), sizeBytes: firstContent.Length);
            NodeFileManifestDto second = RemoteFile("offline/remote-second.txt", Hash(secondContent), sizeBytes: secondContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.DownloadFailureIds.Add(first.Id);
            remoteFiles.DownloadFailureIds.Add(second.Id);
            remoteFiles.Downloads[first.Id] = firstContent;
            remoteFiles.Downloads[second.Id] = secondContent;
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(first, second)),
                remoteFiles,
                stateStore);

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            remoteFiles.DownloadFailureIds.Clear();
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(first, second)),
                remoteFiles,
                stateStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());
            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", first.Metadata["relativePath"]);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", second.Metadata["relativePath"]);

            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(static activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded, SyncActivityKind.Downloaded }));
                Assert.That(File.ReadAllText(Path.Combine(_root, "offline", "remote-first.txt")), Is.EqualTo("first remote content"));
                Assert.That(File.ReadAllText(Path.Combine(_root, "offline", "remote-second.txt")), Is.EqualTo("second remote content"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(firstEntry!.RemoteContentHash, Is.EqualTo(first.ContentHash));
                Assert.That(secondEntry, Is.Not.Null);
                Assert.That(secondEntry!.RemoteContentHash, Is.EqualTo(second.ContentHash));
            });
        }
    }
}
