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
        public async Task RunOnceAsync_DeletesRemoteOnlyWhenBaselineKnowsLocalDelete()
        {
            NodeFileManifestDto remote = RemoteFile("delete-remote.txt", HashText("old"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "delete-remote.txt", remote.ContentHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "delete-remote.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_CanBypassRemoteTrashWhenExplicitlyConfigured()
        {
            NodeFileManifestDto remote = RemoteFile("delete-remote-permanent.txt", HashText("old"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "delete-remote-permanent.txt", remote.ContentHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { DeleteRemotePermanently = true });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "delete-remote-permanent.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, true, remote.ETag) }));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_DoesNotDeleteBaselineWhenRemoteDeleteFails()
        {
            string relativePath = "delete-remote-fails.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.DeleteFailureIds.Add(remote.Id);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, remote.ContentHash, remote);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_RecoversAfterRemoteDeleteBeforeBaselineDelete()
        {
            string relativePath = "remote-deleted-before-baseline.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore durableStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(durableStore, relativePath, remote.ContentHash, remote);
            FailingDeleteStateStore failingStore = new FailingDeleteStateStore(durableStore);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            SyncStateEntry? staleEntry = await durableStore.GetAsync("pair-a", relativePath);
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                durableStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(staleEntry, Is.Not.Null);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_RecoversAfterLocalDeleteBeforeBaselineDelete()
        {
            string relativePath = "local-deleted-before-baseline.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            NodeFileManifestDto remote = RemoteFile(relativePath, local.ContentHash);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SqliteSyncStateStore durableStore = new SqliteSyncStateStore(_databasePath);
            await InsertBaselineAsync(durableStore, relativePath, local.ContentHash, remote);
            FailingDeleteStateStore failingStore = new FailingDeleteStateStore(durableStore);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(local),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            SyncStateEntry? staleEntry = await durableStore.GetAsync("pair-a", relativePath);
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(EmptyRemoteTree()),
                remoteFiles,
                durableStore);
            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(staleEntry, Is.Not.Null);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_BlocksRemoteDeletesOverRunLimit()
        {
            NodeFileManifestDto firstRemote = RemoteFile("a.txt", HashText("old-a"));
            NodeFileManifestDto secondRemote = RemoteFile("b.txt", HashText("old-b"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(firstRemote, secondRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "a.txt", firstRemote.ContentHash, firstRemote);
            await InsertBaselineAsync(stateStore, "b.txt", secondRemote.ContentHash, secondRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", "a.txt");
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", "b.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(result.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_DownloadsRemoteFileInsteadOfDeletingWhenBaselineIsMissing()
        {
            byte[] content = Encoding.UTF8.GetBytes("no-baseline-remote");
            NodeFileManifestDto remote = RemoteFile("safe-download.txt", Hash(content), sizeBytes: content.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = content;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out _);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(File.ReadAllText(Path.Combine(_root, "safe-download.txt")), Is.EqualTo("no-baseline-remote"));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
            });
        }


        [Test]
        public async Task RunOnceAsync_DeletesLocalWhenBaselineKnowsRemoteDelete()
        {
            string relativePath = "delete-local.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, local.ContentHash);
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_BlocksLocalDeletesOverRunLimit()
        {
            WriteFile("a.txt", "old-a");
            WriteFile("b.txt", "old-b");
            LocalFileSnapshot firstLocal = LocalFile("a.txt", "old-a");
            LocalFileSnapshot secondLocal = LocalFile("b.txt", "old-b");
            NodeFileManifestDto firstRemote = RemoteFile("a.txt", firstLocal.ContentHash);
            NodeFileManifestDto secondRemote = RemoteFile("b.txt", secondLocal.ContentHash);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(firstLocal, secondLocal),
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, "a.txt", firstLocal.ContentHash, firstRemote);
            await InsertBaselineAsync(stateStore, "b.txt", secondLocal.ContentHash, secondRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumLocalDeletesPerRun = 1 });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", "a.txt");
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", "b.txt");
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, "a.txt")), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "b.txt")), Is.True);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(result.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_PreservesBothVersionsWhenLocalAndRemoteChanged()
        {
            string relativePath = "conflict.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText("old"), RemoteFile(relativePath, HashText("old"), remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(result.Activities[0].Details, Does.Contain("Cotton conflict"));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.LocalContentHash, Is.Not.EqualTo(entry.RemoteContentHash));
            });
        }
    }
}
