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
        public async Task RunOnceAsync_PropagatesLocalEmptyDirectoryRenameAsCreateAndDelete()
        {
            const string oldPath = "Projects";
            const string newPath = "ProjectsRenamed";
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(oldRemoteDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(newPath),
                },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { newPath }));
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (oldRemoteDirectory.Node.Id, false) }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(state[0].RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.DeletedRemote }));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesMovesRemoteDirectorySubtreeByStableIds()
        {
            const string oldRootPath = "Projects";
            const string oldChildPath = "Projects/Source";
            const string oldFilePath = "Projects/Source/data.bin";
            const string targetParentPath = "Archive";
            const string newRootPath = "Archive/ProjectsRenamed";
            const string newChildPath = "Archive/ProjectsRenamed/Source";
            const string newFilePath = "Archive/ProjectsRenamed/Source/data.bin";
            const string content = "hydrated remote-move content";
            WriteFile(oldFilePath, content);
            Directory.CreateDirectory(Path.Combine(_root, targetParentPath));
            LocalFileSnapshot localFile = LocalFile(oldFilePath, content);
            string localContentHash = localFile.ContentHash;
            localFile.ContentHash = string.Empty;
            localFile.IsCloudFilesPlaceholder = true;
            RemoteDirectorySnapshot targetParent = RemoteDirectory(targetParentPath);
            RemoteDirectorySnapshot oldRoot = RemoteDirectory(oldRootPath);
            RemoteDirectorySnapshot oldChild = RemoteDirectory(oldChildPath, oldRoot.Node.Id);
            RemoteDirectorySnapshot movedRoot = new()
            {
                RelativePath = newRootPath,
                Node = new NodeDto
                {
                    Id = oldRoot.Node.Id,
                    ParentId = targetParent.Node.Id,
                    Name = "ProjectsRenamed",
                },
            };
            RemoteDirectorySnapshot movedChild = new()
            {
                RelativePath = newChildPath,
                Node = new NodeDto
                {
                    Id = oldChild.Node.Id,
                    ParentId = movedRoot.Node.Id,
                    Name = "Source",
                },
            };
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(oldFilePath, localContentHash, remoteFileId, localFile.SizeBytes);
            baselineRemote.NodeId = oldChild.Node.Id;
            localFile.LastWriteUtc = baselineRemote.UpdatedAt;
            NodeFileManifestDto movedRemote = RemoteFile(newFilePath, localContentHash, remoteFileId, localFile.SizeBytes);
            movedRemote.NodeId = movedChild.Node.Id;
            RemoteTreeSnapshot remoteTree = RemoteTree(movedRemote);
            remoteTree.Directories.AddRange([targetParent, movedRoot, movedChild]);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(localFile)
            {
                ContentHashFactory = file =>
                {
                    Assert.That(file.RelativePath, Is.EqualTo(oldFilePath));
                    return localContentHash;
                },
                Directories =
                {
                    LocalDirectory(targetParentPath),
                    LocalDirectory(oldRootPath),
                    LocalDirectory(oldChildPath),
                },
            };
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter
            {
                HydrationState = SyncPlaceholderHydrationState.Hydrated,
                LocalLastWriteUtc = baselineRemote.UpdatedAt.AddMinutes(1),
                LocalSizeBytes = localFile.SizeBytes,
            };
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);
            await InsertDirectoryBaselineAsync(stateStore, targetParentPath, targetParent.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldRootPath, oldRoot.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldChildPath, oldChild.Node);
            await InsertBaselineAsync(stateStore, oldFilePath, localContentHash, baselineRemote, localFile.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths(
                    [
                        oldRootPath,
                        oldChildPath,
                        oldFilePath,
                        newRootPath,
                        newChildPath,
                        newFilePath,
                    ]),
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            string targetFilePath = Path.Combine(_root, newFilePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldRootPath)), Is.False);
                Assert.That(File.Exists(targetFilePath), Is.True);
                Assert.That(File.ReadAllText(targetFilePath), Is.EqualTo(content));
                Assert.That(
                    state.Select(entry => entry.RelativePath),
                    Is.EqualTo(new[] { targetParentPath, newRootPath, newChildPath, newFilePath }));
                Assert.That(state.Single(entry => entry.RelativePath == newRootPath).RemoteNodeId, Is.EqualTo(oldRoot.Node.Id));
                Assert.That(state.Single(entry => entry.RelativePath == newChildPath).RemoteNodeId, Is.EqualTo(oldChild.Node.Id));
                Assert.That(state.Single(entry => entry.RelativePath == newFilePath).RemoteFileId, Is.EqualTo(remoteFileId));
                Assert.That(
                    state.Single(entry => entry.RelativePath == newFilePath).LocalLastWriteUtc,
                    Is.EqualTo(placeholderWriter.LocalLastWriteUtc));
                Assert.That(
                    state.Single(entry => entry.RelativePath == newFilePath).LocalSizeBytes,
                    Is.EqualTo(placeholderWriter.LocalSizeBytes));
                Assert.That(scanner.ContentHashCalls, Is.EqualTo(1));
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { newFilePath }));
                Assert.That(
                    placeholderWriter.CompletedDirectoryTreeRequests.Single().Select(request => request.RelativePath),
                    Is.EqualTo(new[] { newRootPath, newChildPath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(
                    result.Activities.Select(activity => activity.Kind),
                    Is.EqualTo(new[] { SyncActivityKind.Moved, SyncActivityKind.Converged }));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesRemovesRemoteDeletedDirectorySubtreeInOnePass()
        {
            const string rootPath = "DeleteTarget";
            const string childPath = "DeleteTarget/Child";
            const string filePath = "DeleteTarget/Child/data.bin";
            const string content = "unchanged hydrated content";
            WriteFile(filePath, content);
            LocalFileSnapshot localFile = LocalFile(filePath, content);
            localFile.IsCloudFilesPlaceholder = true;
            RemoteDirectorySnapshot remoteRoot = RemoteDirectory(rootPath);
            RemoteDirectorySnapshot remoteChild = RemoteDirectory(childPath, remoteRoot.Node.Id);
            NodeFileManifestDto baselineRemote = RemoteFile(filePath, localFile.ContentHash, sizeBytes: localFile.SizeBytes);
            baselineRemote.NodeId = remoteChild.Node.Id;
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(localFile)
            {
                Directories =
                {
                    LocalDirectory(rootPath),
                    LocalDirectory(childPath),
                },
            };
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, remoteRoot.Node);
            await InsertDirectoryBaselineAsync(stateStore, childPath, remoteChild.Node);
            await InsertBaselineAsync(stateStore, filePath, localFile.ContentHash, baselineRemote, localFile.SizeBytes);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath, childPath, filePath]),
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, rootPath)), Is.False);
                Assert.That(state, Is.Empty);
                Assert.That(
                    result.Activities.Select(activity => (activity.Kind, activity.RelativePath)),
                    Is.EqualTo(new[]
                    {
                        (SyncActivityKind.DeletedLocal, filePath),
                        (SyncActivityKind.DeletedLocal, childPath),
                        (SyncActivityKind.DeletedLocal, rootPath),
                    }));
            });
        }


        [Test]
        public async Task RunOnceAsync_PropagatesRemoteEmptyDirectoryRenameAsCreateAndDelete()
        {
            const string oldPath = "Projects";
            const string newPath = "ProjectsRenamed";
            Directory.CreateDirectory(Path.Combine(_root, oldPath));
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteDirectorySnapshot newRemoteDirectory = RemoteDirectory(newPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(newRemoteDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(oldPath),
                },
            };
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldPath)), Is.False);
                Assert.That(Directory.Exists(Path.Combine(_root, newPath)), Is.True);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { newPath }));
                Assert.That(state[0].RemoteNodeId, Is.EqualTo(newRemoteDirectory.Node.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded, SyncActivityKind.DeletedLocal }));
            });
        }


        [Test]
        public async Task RunOnceAsync_PropagatesLocalEmptyDirectoryMoveAsCreateAndDelete()
        {
            const string parentPath = "Archive";
            const string oldPath = "Projects";
            const string newPath = "Archive/Projects";
            RemoteDirectorySnapshot remoteParent = RemoteDirectory(parentPath);
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteParent);
            remoteTree.Directories.Add(oldRemoteDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(parentPath),
                    LocalDirectory(newPath),
                },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, parentPath, remoteParent.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates, Has.Count.EqualTo(1));
                Assert.That(remoteDirectories.Creates[0].ParentNodeId, Is.EqualTo(remoteParent.Node.Id));
                Assert.That(remoteDirectories.Creates[0].Name, Is.EqualTo("Projects"));
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (oldRemoteDirectory.Node.Id, false) }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { parentPath, newPath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.DeletedRemote }));
            });
        }
    }
}
