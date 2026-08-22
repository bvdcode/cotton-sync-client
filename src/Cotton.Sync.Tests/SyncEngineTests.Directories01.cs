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
        public void RunOnceAsync_FailsBeforeDownloadWhenPlannedDownloadsExceedFreeSpace()
        {
            NodeFileManifestDto remote = RemoteFile("huge.bin", HashText("huge"), sizeBytes: long.MaxValue);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out _);

            LocalInsufficientDiskSpaceException? exception = Assert.ThrowsAsync<LocalInsufficientDiskSpaceException>(
                () => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("Not enough disk space"));
                Assert.That(exception?.Message, Does.Contain("huge.bin"));
                Assert.That(exception?.RelativePath, Is.EqualTo("huge.bin"));
                Assert.That(exception?.RequiredBytes, Is.EqualTo(long.MaxValue));
                Assert.That(File.Exists(Path.Combine(_root, "huge.bin")), Is.False);
            });
        }


        [Test]
        public async Task RunOnceAsync_CreatesRemoteFolderForLocalOnlyEmptyDirectoryAndStoresBaseline()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects", "Archive"));
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Creates, Has.Count.EqualTo(2));
                Assert.That(remoteDirectories.Creates[0].ParentNodeId, Is.EqualTo(_remoteRootNodeId));
                Assert.That(remoteDirectories.Creates[0].Name, Is.EqualTo("Projects"));
                Assert.That(remoteDirectories.Creates[1].ParentNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(remoteDirectories.Creates[1].Name, Is.EqualTo("Archive"));
                Assert.That(state.Select(entry => entry.Kind), Is.EqualTo(new[] { SyncEntryKind.Directory, SyncEntryKind.Directory }));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(state.Select(entry => entry.RemoteNodeId), Is.EqualTo(remoteDirectories.Creates.Select(call => call.ReturnedNode.Id)));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Uploaded }));
            });
        }


        [Test]
        public async Task RunOnceAsync_ReusesExistingRemoteFolderWhenLocalCreateHitsConflict()
        {
            NodeDto existingProjectsNode = new NodeDto
            {
                Id = Guid.NewGuid(),
                ParentId = _remoteRootNodeId,
                Name = "Projects",
            };
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                    LocalDirectory("Projects/Archive"),
                },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            remoteDirectories.ConflictCreates.Add((_remoteRootNodeId, "Projects"));
            remoteDirectories.ExistingDirectories.Add(existingProjectsNode);
            SyncEngine engine = CreateEngine(
                scanner,
                EmptyRemoteTree(),
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.CreateAttempts.Select(call => call.Name), Is.EqualTo(new[] { "Projects", "Archive" }));
                Assert.That(remoteDirectories.FindChildDirectoryCalls, Is.EqualTo(new[] { (_remoteRootNodeId, "Projects") }));
                Assert.That(remoteDirectories.Creates, Has.Count.EqualTo(1));
                Assert.That(remoteDirectories.Creates[0].ParentNodeId, Is.EqualTo(existingProjectsNode.Id));
                Assert.That(remoteDirectories.Creates[0].Name, Is.EqualTo("Archive"));
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Projects", "Projects/Archive" }));
                Assert.That(state.Select(entry => entry.RemoteNodeId), Is.EqualTo(new[] { existingProjectsNode.Id, remoteDirectories.Creates[0].ReturnedNode.Id }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Uploaded }));
                Assert.That(result.Activities[0].Details, Does.Contain("Reused existing remote folder"));
            });
        }


        [Test]
        public async Task RunOnceAsync_CreatesLocalFolderForRemoteOnlyEmptyDirectoryAndStoresBaseline()
        {
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(state, Has.Count.EqualTo(1));
                Assert.That(state[0].Kind, Is.EqualTo(SyncEntryKind.Directory));
                Assert.That(state[0].RelativePath, Is.EqualTo("Projects"));
                Assert.That(state[0].RemoteNodeId, Is.EqualTo(remoteDirectory.Node.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded }));
            });
        }


        [Test]
        public async Task RunOnceAsync_DeletesRemoteEmptyDirectoryWhenBaselineKnowsLocalDelete()
        {
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteDirectory);
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.EqualTo(new[] { (remoteDirectory.Node.Id, false) }));
                Assert.That(state, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
            });
        }


        [Test]
        public async Task RunOnceAsync_DeletesLocalEmptyDirectoryWhenBaselineKnowsRemoteDelete()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects"));
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                },
            };
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.False);
                Assert.That(state, Is.Empty);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedLocal }));
            });
        }


        [Test]
        public async Task RunOnceAsync_SkipsLocalDirectoryDeleteWhenFolderIsNotEmpty()
        {
            WriteFile("Projects/keep.txt", "keep");
            RemoteDirectorySnapshot remoteDirectory = RemoteDirectory("Projects");
            LocalFileSnapshot localFile = LocalFile("Projects/keep.txt", "keep");
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(localFile)
            {
                Directories =
                {
                    LocalDirectory("Projects"),
                },
            };
            SyncEngine engine = CreateEngine(scanner, EmptyRemoteTree(), new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, "Projects", remoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", "Projects");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, "Projects")), Is.True);
                Assert.That(File.Exists(Path.Combine(_root, "Projects", "keep.txt")), Is.True);
                Assert.That(state, Is.Not.Null);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped, SyncActivityKind.Uploaded }));
                Assert.That(result.Activities[0].RequiresUserAction, Is.False);
                Assert.That(result.Activities[0].Details, Does.Contain("not empty"));
            });
        }


        [Test]
        public async Task RunOnceAsync_BlocksRemoteDirectoryDeletesOverRunLimit()
        {
            RemoteDirectorySnapshot first = RemoteDirectory("One");
            RemoteDirectorySnapshot second = RemoteDirectory("Two");
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(first);
            remoteTree.Directories.Add(second);
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, "One", first.Node);
            await InsertDirectoryBaselineAsync(stateStore, "Two", second.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair(), new SyncRunOptions { MaximumRemoteDeletesPerRun = 1 });

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "One", "Two" }));
                Assert.That(result.RequiresUserAction, Is.True);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped, SyncActivityKind.Skipped }));
                Assert.That(result.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(result.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(result.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
            });
        }
    }
}
