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
        public async Task RunOnceAsync_PropagatesRemoteEmptyDirectoryMoveAsCreateAndDelete()
        {
            const string parentPath = "Archive";
            const string oldPath = "Projects";
            const string newPath = "Archive/Projects";
            Directory.CreateDirectory(Path.Combine(_root, parentPath));
            Directory.CreateDirectory(Path.Combine(_root, oldPath));
            RemoteDirectorySnapshot remoteParent = RemoteDirectory(parentPath);
            RemoteDirectorySnapshot oldRemoteDirectory = RemoteDirectory(oldPath);
            RemoteDirectorySnapshot movedRemoteDirectory = RemoteDirectory(newPath, remoteParent.Node.Id);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteParent);
            remoteTree.Directories.Add(movedRemoteDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(parentPath),
                    LocalDirectory(oldPath),
                },
            };
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(), out SqliteSyncStateStore stateStore);
            await InsertDirectoryBaselineAsync(stateStore, parentPath, remoteParent.Node);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, oldRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldPath)), Is.False);
                Assert.That(Directory.Exists(Path.Combine(_root, newPath.Replace('/', Path.DirectorySeparatorChar))), Is.True);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { parentPath, newPath }));
                Assert.That(state.Single(entry => entry.RelativePath == newPath).RemoteNodeId, Is.EqualTo(movedRemoteDirectory.Node.Id));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Downloaded, SyncActivityKind.DeletedLocal }));
            });
        }


        [Test]
        public async Task RunOnceAsync_PreservesBothDirectoryRenamesWhenLocalAndRemoteRenameDiverge()
        {
            const string oldPath = "Projects";
            const string localRenamePath = "Projects Local";
            const string remoteRenamePath = "Projects Remote";
            Directory.CreateDirectory(Path.Combine(_root, localRenamePath));
            RemoteDirectorySnapshot baselineRemoteDirectory = RemoteDirectory(oldPath);
            RemoteDirectorySnapshot remoteRenamedDirectory = RemoteDirectory(remoteRenamePath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(remoteRenamedDirectory);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner
            {
                Directories =
                {
                    LocalDirectory(localRenamePath),
                },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new FakeRemoteDirectorySynchronizer();
            SyncEngine engine = CreateEngine(
                scanner,
                remoteTree,
                new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore,
                remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, oldPath, baselineRemoteDirectory.Node);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncStateEntry> state = await stateStore.LoadPairAsync("pair-a");
            SyncStateEntry? oldEntry = await stateStore.GetAsync("pair-a", oldPath);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(Path.Combine(_root, oldPath)), Is.False);
                Assert.That(Directory.Exists(Path.Combine(_root, localRenamePath)), Is.True);
                Assert.That(Directory.Exists(Path.Combine(_root, remoteRenamePath)), Is.True);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Creates.Select(call => call.Name), Is.EqualTo(new[] { localRenamePath }));
                Assert.That(oldEntry, Is.Null);
                Assert.That(state.Select(entry => entry.RelativePath), Is.EqualTo(new[] { localRenamePath, remoteRenamePath }));
                Assert.That(state.Single(entry => entry.RelativePath == localRenamePath).RemoteNodeId, Is.EqualTo(remoteDirectories.Creates[0].ReturnedNode.Id));
                Assert.That(state.Single(entry => entry.RelativePath == remoteRenamePath).RemoteNodeId, Is.EqualTo(remoteRenamedDirectory.Node.Id));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Uploaded, SyncActivityKind.Downloaded }));
            });
        }
    }
}
