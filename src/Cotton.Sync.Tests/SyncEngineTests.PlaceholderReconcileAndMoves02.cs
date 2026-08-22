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
        public async Task RunOnceAsync_WithWindowsVirtualFilesPreservesMaterializedLocalWhenRemotePlaceholderIsDeleted()
        {
            const string relativePath = "remote-deleted-materialized.txt";
            const string localContent = "local replacement";
            WriteFile(relativePath, localContent);
            LocalFileSnapshot local = LocalFile(relativePath, localContent);
            local.IsCloudFilesPlaceholder = true;
            local.IsCloudFilesOnlineOnlyPlaceholder = false;
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
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile, Is.Null);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(File.Exists(fullPath), Is.True);
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo(localContent));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.None));
                Assert.That(entry.PlaceholderIdentity, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMovesRemoteOnlyPlaceholderWhenRemotePathChanges()
        {
            const string oldPath = "Docs/old-name.txt";
            const string newPath = "Docs/new-name.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto movedRemote = RemoteFile(newPath, baselineRemote.ContentHash, remoteFileId, sizeBytes: 1024);
            WriteFile(oldPath, string.Empty);
            LocalFileSnapshot oldLocalPlaceholder = CloudFilesPlaceholderLocal(oldPath, baselineRemote.SizeBytes);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(oldLocalPlaceholder),
                RemoteTree(movedRemote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EquivalentTo(new[]
                {
                    SyncActivityKind.DeletedLocal,
                    SyncActivityKind.PlaceholderCreated,
                }));
                Assert.That(File.Exists(Path.Combine(_root, oldPath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(File.Exists(Path.Combine(_root, newPath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Not.Null);
                Assert.That(newEntry!.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(newEntry.RemoteContentHash, Is.EqualTo(baselineRemote.ContentHash));
                Assert.That(newEntry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMovesOnlineOnlyPlaceholderDuringParentRename()
        {
            const string oldDirectoryPath = "Library";
            const string newDirectoryPath = "LibraryRenamed";
            const string oldPath = "Library/online-only.bin";
            const string newPath = "LibraryRenamed/online-only.bin";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(oldDirectoryPath);
            RemoteTreeSnapshot remoteTree = RemoteTree(remote);
            remoteTree.Directories.Add(remoteDirectory);
            LocalFileSnapshot movedLocalPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            movedLocalPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(movedLocalPlaceholder);
            scanner.Directories.Add(LocalDirectory(newDirectoryPath));
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertDirectoryBaselineAsync(stateStore, oldDirectoryPath, remoteDirectory.Node);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldDirectoryPath, newDirectoryPath]),
                });

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(remoteFiles.Moves[0].ExistingRemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(scanner.LastIncludeDirectoryDescendants, Is.False);
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(placeholderWriter.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholderWriter.Requests[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(placeholderWriter.Requests[0].RemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    result.Activities.Count(activity =>
                        activity.Kind == SyncActivityKind.Moved
                        && string.Equals(activity.RelativePath, newPath, StringComparison.OrdinalIgnoreCase)),
                    Is.EqualTo(1));
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { newDirectoryPath }));
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (remoteDirectory.Node.Id, false) }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(newEntry, Is.Not.Null);
                Assert.That(newEntry!.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(newEntry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(newEntry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(newEntry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRenamesOnlineOnlyPlaceholderWithinDirectory()
        {
            const string oldPath = "Online/rename.bin";
            const string newPath = "Online/renamed.bin";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            LocalFileSnapshot renamedLocalPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            renamedLocalPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(renamedLocalPlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(remoteFiles.Moves[0].ExistingRemoteFile.Id, Is.EqualTo(remoteFileId));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    result.Activities.Select(activity => activity.Kind),
                    Is.EqualTo(new[] { SyncActivityKind.Moved }));
                Assert.That(oldState, Is.Null);
                Assert.That(newState, Is.Not.Null);
                Assert.That(newState!.RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(newState.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(newState.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRenamesRootLevelOnlineOnlyPlaceholder()
        {
            const string oldPath = "rename.bin";
            const string newPath = "renamed.bin";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), remoteFileId, sizeBytes: 1024);
            LocalFileSnapshot renamedLocalPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            renamedLocalPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(renamedLocalPlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newPath));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Moved }));
                Assert.That(oldState, Is.Null);
                Assert.That(newState, Is.Not.Null);
                Assert.That(newState!.RemoteFileId, Is.EqualTo(remoteFileId));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotRenameExplicitlyDeletedPlaceholder()
        {
            const string oldPath = "Online/delete.bin";
            const string newPath = "Online/replacement.bin";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot replacementPlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            replacementPlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(replacementPlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath], [oldPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(oldState, Is.Null);
                Assert.That(newState, Is.Null);
            });
        }
    }
}
