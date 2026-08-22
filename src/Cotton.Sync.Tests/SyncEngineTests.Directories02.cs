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
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDeletesLocalDirectorySubtreeInOnePass()
        {
            const string rootPath = "Library";
            const string nestedPath = "Library/Disc1";
            const string deepPath = "Library/Disc1/Disc2";
            const string emptyPath = "Library/Empty";
            const string rootFilePath = "Library/root.bin";
            const string nestedFilePath = "Library/Disc1/Disc2/nested.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            RemoteDirectorySnapshot nested = RemoteDirectory(nestedPath, root.Node.Id);
            RemoteDirectorySnapshot deep = RemoteDirectory(deepPath, nested.Node.Id);
            RemoteDirectorySnapshot empty = RemoteDirectory(emptyPath, root.Node.Id);
            NodeFileManifestDto rootFile = RemoteFile(rootFilePath, HashText("root-content"), sizeBytes: 1024);
            NodeFileManifestDto nestedFile = RemoteFile(nestedFilePath, HashText("nested-content"), sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(rootFile, nestedFile);
            remoteTree.Directories.Add(root);
            remoteTree.Directories.Add(nested);
            remoteTree.Directories.Add(deep);
            remoteTree.Directories.Add(empty);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertDirectoryBaselineAsync(stateStore, nestedPath, nested.Node);
            await InsertDirectoryBaselineAsync(stateStore, deepPath, deep.Node);
            await InsertDirectoryBaselineAsync(stateStore, emptyPath, empty.Node);
            await InsertPlaceholderBaselineAsync(stateStore, rootFilePath, rootFile);
            await InsertPlaceholderBaselineAsync(stateStore, nestedFilePath, nestedFile);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(
                    remoteFiles.Deletes.Select(call => call.NodeFileId),
                    Is.EquivalentTo(new[] { rootFile.Id, nestedFile.Id }));
                Assert.That(
                    remoteDirectories.Deletes,
                    Is.EqualTo(new[]
                    {
                        (deep.Node.Id, false),
                        (nested.Node.Id, false),
                        (empty.Node.Id, false),
                        (root.Node.Id, false),
                    }));
                Assert.That(state, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Any(activity => activity.Kind == SyncActivityKind.Skipped), Is.False);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotCascadeDeletedSubtreeWithUntrackedRemoteFile()
        {
            const string rootPath = "Library";
            const string trackedPath = "Library/tracked.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            NodeFileManifestDto tracked = RemoteFile(trackedPath, HashText("tracked-content"), sizeBytes: 1024);
            NodeFileManifestDto untracked = RemoteFile(
                "Library/untracked.bin",
                HashText("untracked-content"),
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(tracked, untracked);
            remoteTree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                remoteFilePlaceholderWriter: new FakeRemoteFilePlaceholderWriter());
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertPlaceholderBaselineAsync(stateStore, trackedPath, tracked);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                });

            SyncStateEntry? rootState = await stateStore.GetAsync("pair-a", rootPath);
            SyncStateEntry? trackedState = await stateStore.GetAsync("pair-a", trackedPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(rootState, Is.Not.Null);
                Assert.That(trackedState, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesDoesNotCascadeDeletedSubtreeWithChangedRemoteFile()
        {
            const string rootPath = "Library";
            const string filePath = "Library/tracked.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            NodeFileManifestDto baseline = RemoteFile(filePath, HashText("baseline-content"), sizeBytes: 1024);
            NodeFileManifestDto changed = RemoteFile(
                filePath,
                HashText("changed-content"),
                baseline.Id,
                sizeBytes: 2048);
            RemoteTreeSnapshot remoteTree = RemoteTree(changed);
            remoteTree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertPlaceholderBaselineAsync(stateStore, filePath, baseline);

            await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                });

            SyncStateEntry? rootState = await stateStore.GetAsync("pair-a", rootPath);
            SyncStateEntry? fileState = await stateStore.GetAsync("pair-a", filePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(rootState, Is.Not.Null);
                Assert.That(fileState, Is.Not.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesCountsDeletedSubtreeDirectoriesInRemoteDeleteGuard()
        {
            const string rootPath = "Library";
            const string filePath = "Library/tracked.bin";
            RemoteDirectorySnapshot root = RemoteDirectory(rootPath);
            NodeFileManifestDto remoteFile = RemoteFile(filePath, HashText("tracked-content"), sizeBytes: 1024);
            RemoteTreeSnapshot remoteTree = RemoteTree(remoteFile);
            remoteTree.Directories.Add(root);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            SyncEngine engine = new(
                new FakeLocalFileScanner(),
                new DescendantPathRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, rootPath, root.Node);
            await InsertPlaceholderBaselineAsync(stateStore, filePath, remoteFile);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForLocalChangedPaths([rootPath], [rootPath]),
                    MaximumRemoteDeletesPerRun = 1,
                });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(state, Has.Count.EqualTo(2));
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities, Has.Count.EqualTo(1));
                Assert.That(result.Activities[0].Kind, Is.EqualTo(SyncActivityKind.Skipped));
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
            });
        }


        [Test]
        public async Task RunOnceAsync_DoesNotCascadeRemoteDirectoryDeletesInsideOneRun()
        {
            RemoteDirectorySnapshot parent = RemoteDirectory("Projects");
            RemoteDirectorySnapshot child = RemoteDirectory("Projects/Archive", parent.Node.Id);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(parent);
            remoteTree.Directories.Add(child);
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", parent.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Projects/Archive", child.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (child.Node.Id, false) }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects" }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote, SyncActivityKind.Skipped }));
                Assert.That(result.Activities[1].Details, Does.Contain("not empty"));
            });
        }


        [Test]
        public async Task RunOnceAsync_BlocksWholeRemoteDeletedDirectorySubtreeOverRunLimit()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects", "Archive"));
            RemoteDirectorySnapshot parent = RemoteDirectory("Projects");
            RemoteDirectorySnapshot child = RemoteDirectory("Projects/Archive", parent.Node.Id);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", parent.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Projects/Archive", child.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumLocalDeletesPerRun = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects", "Archive")), Is.True);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped, SyncActivityKind.Skipped }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities.Select(activity => activity.Details), Is.All.Contains("2 pending deletes exceed limit 1"));
            });
        }


        [Test]
        public async Task RunOnceAsync_PreservesLocalFolderWhenRemoteFileInsideIsDeleted()
        {
            const string directoryPath = "Projects";
            const string filePath = "Projects/deleted-remotely.txt";
            WriteFile(filePath, "baseline-content");
            LocalFileSnapshot local = LocalFile(filePath, "baseline-content");
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory(directoryPath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local)
            {
                Directories =
                {
                    LocalDirectory(directoryPath),
                },
            };
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, directoryPath, remoteDirectory.Node);
            await InsertBaselineAsync(
                stateStore,
                filePath,
                local.ContentHash,
                RemoteFile(filePath, local.ContentHash));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, directoryPath)), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "Projects", "deleted-remotely.txt")), Is.False);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { directoryPath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
            });
        }
    }
}
