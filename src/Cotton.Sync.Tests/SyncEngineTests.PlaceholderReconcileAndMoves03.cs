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
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotRenameChangedNameAcrossDirectories()
        {
            const string oldPath = "First/old.bin";
            const string newPath = "Second/new.bin";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot candidatePlaceholder = CloudFilesPlaceholderLocal(newPath, remote.SizeBytes);
            candidatePlaceholder.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new(candidatePlaceholder);
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldPath, newPath]),
                });

            SyncStateEntry? oldState = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? newState = await stateStore.GetAsync("pair-a", newPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(oldState, Is.Not.Null);
                Assert.That(oldState!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(newState, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesMovesNestedDirectorySubtreeInOnePass()
        {
            const string oldDirectoryPath = "Source/Album";
            const string oldNestedDirectoryPath = "Source/Album/Disc1";
            const string newDirectoryPath = "Target/Album";
            const string newNestedDirectoryPath = "Target/Album/Disc1";
            const string oldFilePath = "Source/Album/Disc1/online-only.bin";
            const string newFilePath = "Target/Album/Disc1/online-only.bin";
            RemoteDirectorySnapshot sourceDirectory = RemoteDirectory("Source");
            RemoteDirectorySnapshot targetDirectory = RemoteDirectory("Target");
            RemoteDirectorySnapshot oldDirectory = RemoteDirectory(oldDirectoryPath, sourceDirectory.Node.Id);
            RemoteDirectorySnapshot oldNestedDirectory = RemoteDirectory(
                oldNestedDirectoryPath,
                oldDirectory.Node.Id);
            NodeFileManifestDto remoteFile = RemoteFile(
                oldFilePath,
                HashText("remote-content"),
                sizeBytes: 1024);
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.Add(sourceDirectory);
            remoteTree.Directories.Add(targetDirectory);
            remoteTree.Directories.Add(oldDirectory);
            remoteTree.Directories.Add(oldNestedDirectory);
            LocalFileSnapshot movedPlaceholder = CloudFilesPlaceholderLocal(newFilePath, remoteFile.SizeBytes);
            movedPlaceholder.LastWriteUtc = remoteFile.UpdatedAt;
            FakeLocalFileScanner scanner = new(movedPlaceholder);
            scanner.Directories.Add(LocalDirectory("Source"));
            scanner.Directories.Add(LocalDirectory("Target"));
            scanner.Directories.Add(LocalDirectory(newDirectoryPath));
            scanner.Directories.Add(LocalDirectory(newNestedDirectoryPath));
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
            await InsertDirectoryBaselineAsync(stateStore, "Source", sourceDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Target", targetDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldDirectoryPath, oldDirectory.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldNestedDirectoryPath, oldNestedDirectory.Node);
            await InsertPlaceholderBaselineAsync(stateStore, oldFilePath, remoteFile);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths(
                    [
                        "Source",
                        oldDirectoryPath,
                        oldNestedDirectoryPath,
                        oldFilePath,
                        "Target",
                        newDirectoryPath,
                        newNestedDirectoryPath,
                        newFilePath,
                    ]),
                });

            SyncStateEntry? oldDirectoryState = await stateStore.GetAsync("pair-a", oldDirectoryPath);
            SyncStateEntry? oldNestedDirectoryState = await stateStore.GetAsync("pair-a", oldNestedDirectoryPath);
            SyncStateEntry? oldFileState = await stateStore.GetAsync("pair-a", oldFilePath);
            SyncStateEntry? newDirectoryState = await stateStore.GetAsync("pair-a", newDirectoryPath);
            SyncStateEntry? newNestedDirectoryState = await stateStore.GetAsync("pair-a", newNestedDirectoryPath);
            SyncStateEntry? newFileState = await stateStore.GetAsync("pair-a", newFilePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { "Album", "Disc1" }));
                Assert.That(
                    remoteDirectories.Deletes,
                    Is.EqualTo(new[]
                    {
                        (oldNestedDirectory.Node.Id, false),
                        (oldDirectory.Node.Id, false),
                    }));
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Moves[0].RelativePath, Is.EqualTo(newFilePath));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Skipped), Is.False);
                Assert.That(oldDirectoryState, Is.Null);
                Assert.That(oldNestedDirectoryState, Is.Null);
                Assert.That(oldFileState, Is.Null);
                Assert.That(newDirectoryState?.RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(newNestedDirectoryState?.RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[1].ReturnedNode.Id));
                Assert.That(newFileState?.RemoteFileId, Is.EqualTo(remoteFile.Id));
                Assert.That(newFileState?.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotDeleteSubtreeWithUntrackedRemoteDescendant()
        {
            const string oldDirectoryPath = "Library";
            const string newDirectoryPath = "LibraryMoved";
            const string oldTrackedFilePath = "Library/tracked.bin";
            const string newTrackedFilePath = "LibraryMoved/tracked.bin";
            RemoteDirectorySnapshot oldDirectory = RemoteDirectory(oldDirectoryPath);
            NodeFileManifestDto trackedRemoteFile = RemoteFile(
                oldTrackedFilePath,
                HashText("tracked-content"),
                sizeBytes: 1024);
            NodeFileManifestDto untrackedRemoteFile = RemoteFile(
                "Library/untracked.bin",
                HashText("untracked-content"),
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(trackedRemoteFile, untrackedRemoteFile);
            remoteTree.Directories.Add(oldDirectory);
            LocalFileSnapshot movedPlaceholder = CloudFilesPlaceholderLocal(
                newTrackedFilePath,
                trackedRemoteFile.SizeBytes);
            movedPlaceholder.LastWriteUtc = trackedRemoteFile.UpdatedAt;
            FakeLocalFileScanner scanner = new(movedPlaceholder);
            scanner.Directories.Add(LocalDirectory(newDirectoryPath));
            DescendantPathRemoteTreeCrawler crawler = new(remoteTree);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());
            await InsertDirectoryBaselineAsync(stateStore, oldDirectoryPath, oldDirectory.Node);
            await InsertPlaceholderBaselineAsync(stateStore, oldTrackedFilePath, trackedRemoteFile);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([oldDirectoryPath, newDirectoryPath]),
                });

            SyncStateEntry? oldDirectoryState = await stateStore.GetAsync("pair-a", oldDirectoryPath);
            SyncStateEntry? oldTrackedFileState = await stateStore.GetAsync("pair-a", oldTrackedFilePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(oldDirectoryState, Is.Not.Null);
                Assert.That(oldTrackedFileState, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesDoesNotGuessAmbiguousOnlineOnlyPlaceholderMove()
        {
            const string oldPath = "Library/online-only.bin";
            NodeFileManifestDto remote = RemoteFile(oldPath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot firstCandidate = CloudFilesPlaceholderLocal("First/online-only.bin", remote.SizeBytes);
            firstCandidate.LastWriteUtc = remote.UpdatedAt;
            LocalFileSnapshot secondCandidate = CloudFilesPlaceholderLocal("Second/online-only.bin", remote.SizeBytes);
            secondCandidate.LastWriteUtc = remote.UpdatedAt;
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(firstCandidate, secondCandidate),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());
            await InsertPlaceholderBaselineAsync(stateStore, oldPath, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", firstCandidate.RelativePath);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", secondCandidate.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.ActionRequiredMessage, Does.Contain("deleted or moved locally"));
                Assert.That(oldEntry, Is.Not.Null);
                Assert.That(firstEntry, Is.Null);
                Assert.That(secondEntry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesDoesNotUploadRenamedRemoteOnlyPlaceholder()
        {
            NodeFileManifestDto remote = RemoteFile("placeholder-renamed.txt", HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot renamedLocal = LocalFile("aaa-renamed-placeholder.txt", "remote-content");
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(renamedLocal),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertPlaceholderBaselineAsync(stateStore, "placeholder-renamed.txt", remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", "placeholder-renamed.txt");
            SyncStateEntry? newEntry = await stateStore.GetAsync("pair-a", "aaa-renamed-placeholder.txt");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(result.ActionRequiredMessage, Does.Contain("deleted or moved locally"));
                Assert.That(oldEntry, Is.Not.Null);
                Assert.That(newEntry, Is.Null);
            });
        }
    }
}
