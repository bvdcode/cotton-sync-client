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
        public async Task RunOnceAsync_WithWindowsVirtualFilesRestoresMissingRemoteOnlyPlaceholderDuringFullReconcile()
        {
            NodeFileManifestDto remote = RemoteFile("placeholder-deleted.txt", HashText("remote-content"), sizeBytes: 1024);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, "placeholder-deleted.txt", remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { RestoreMissingRemoteOnlyPlaceholders = true });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "placeholder-deleted.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath),
                    Is.EqualTo(new[] { "placeholder-deleted.txt" }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(x => x.Kind),
                    Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesKeepsExistingCloudFilesPlaceholderDuringUnrelatedLocalCreate()
        {
            const string existingPath = "local-upload.txt";
            NodeFileManifestDto existingRemote = RemoteFile(existingPath, HashText("remote-content"), sizeBytes: 79);
            LocalFileSnapshot existingPlaceholder = CloudFilesPlaceholderLocal(existingPath, existingRemote.SizeBytes);
            LocalFileSnapshot newLocal = LocalFile("remote-origin.txt", "Cotton Sync Desktop live smoke from client B");
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(existingPlaceholder, newLocal),
                RemoteTree(existingRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, existingPath, existingRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? placeholderEntry = await stateStore.GetAsync("pair-a", existingPath);
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads.Select(upload => upload.LocalFile.RelativePath), Is.EqualTo(new[] { newLocal.RelativePath }));
                Assert.That(placeholderEntry, Is.Not.Null);
                Assert.That(placeholderEntry!.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(placeholderEntry.RemoteContentHash, Is.EqualTo(existingRemote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesRemoteOnlyPlaceholderWhenRemoteChanges()
        {
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile("remote-updated.txt", HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto changedRemote = RemoteFile("remote-updated.txt", HashText("new-content"), remoteFileId, sizeBytes: 2048);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, "remote-updated.txt", baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-updated.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo("remote-updated.txt"));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.ContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(changedRemote.SizeBytes));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesExistingCloudFilesPlaceholderWhenRemoteChanges()
        {
            const string relativePath = "remote-updated.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto changedRemote = RemoteFile(relativePath, HashText("new-content"), remoteFileId, sizeBytes: 2048);
            LocalFileSnapshot localPlaceholder = CloudFilesPlaceholderLocal(relativePath, baselineRemote.SizeBytes);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(localPlaceholder),
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.ContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesAdoptsUnchangedHydratedPlaceholderWithoutUpload()
        {
            const string relativePath = "remote-hydrated.txt";
            const string content = "remote-content";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText(content), remoteFileId, sizeBytes: Encoding.UTF8.GetByteCount(content));
            LocalFileSnapshot local = LocalFile(relativePath, content);
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Converged }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.LocalSizeBytes, Is.EqualTo(local.SizeBytes));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(entry.PlaceholderIdentity, Is.Not.Null.And.Not.Empty);
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadsMaterializedCloudFileOverRemoteOnlyBaseline()
        {
            const string relativePath = "remote-updated.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto currentRemote = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            LocalFileSnapshot local = LocalFile(relativePath, "local replacement");
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                RemoteTree(currentRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile?.Id, Is.EqualTo(remoteFileId));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.None));
                Assert.That(entry.PlaceholderIdentity, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRefreshesDehydratedPlaceholderWhenRemoteChanges()
        {
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile("remote-updated.txt", HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto changedRemote = RemoteFile("remote-updated.txt", HashText("new-content"), remoteFileId, sizeBytes: 2048);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                RemoteTree(changedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(
                stateStore,
                "remote-updated.txt",
                baselineRemote,
                SyncPlaceholderHydrationState.Dehydrated);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", "remote-updated.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo("remote-updated.txt"));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.ContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteContentHash, Is.EqualTo(changedRemote.ContentHash));
                Assert.That(entry.RemoteSizeBytes, Is.EqualTo(changedRemote.SizeBytes));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRemovesLocalPlaceholderWhenRemoteIsDeleted()
        {
            const string relativePath = "remote-deleted-placeholder.txt";
            WriteFile(relativePath, string.Empty);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, 1024);
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                EmptyRemoteTree(),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string[] tombstones = Directory.GetFiles(
                Path.Combine(_root, ".cotton-sync", "deleted"),
                "*",
                SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(tombstones.Select(Path.GetFileName), Does.Contain(relativePath));
                Assert.That(entry, Is.Null);
            });
        }
    }
}
