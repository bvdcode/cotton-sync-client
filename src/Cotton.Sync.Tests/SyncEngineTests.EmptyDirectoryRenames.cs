// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(SyncPairMaterializationMode.FullMirror, false)]
        [TestCase(SyncPairMaterializationMode.FullMirror, true)]
        [TestCase(SyncPairMaterializationMode.WindowsVirtualFiles, false)]
        [TestCase(SyncPairMaterializationMode.WindowsVirtualFiles, true)]
        public async Task RunOnceAsync_ObservedEmptyDirectoryRename_PreservesRemoteNodeId(
            SyncPairMaterializationMode mode, bool fullReconcile)
        {
            const string sourcePath = "New folder";
            const string temporaryPath = "Temporary name";
            const string targetPath = "Reports";
            RemoteDirectorySnapshot original = RemoteDirectory(sourcePath);
            RemoteTreeSnapshot remoteTree = EmptyRemoteTree();
            remoteTree.Directories.Add(original);
            FakeLocalFileScanner scanner = new()
            {
                Directories = { LocalDirectory(targetPath) },
            };
            FakeRemoteDirectorySynchronizer remoteDirectories = new();
            remoteDirectories.ExistingDirectories.Add(original.Node);
            SyncEngine engine = CreateEngine(scanner, remoteTree, new FakeRemoteFileSynchronizer(),
                out SqliteSyncStateStore stateStore, remoteDirectories);
            await InsertDirectoryBaselineAsync(stateStore, sourcePath, original.Node);
            LocalPathRename[] renames =
            [
                new(sourcePath, temporaryPath),
                new(temporaryPath, targetPath),
            ];
            SyncRunOptions options = new()
            {
                Scope = fullReconcile
                    ? SyncRunScope.ForFull(renames)
                    : SyncRunScope.ForLocalChangedPaths([sourcePath, targetPath], [], renames),
            };

            SyncRunResult result = await engine.RunOnceAsync(Pair(mode), options);

            SyncStateEntry? target = await stateStore.GetAsync("pair-a", targetPath);
            Assert.Multiple(() =>
            {
                Assert.That(target?.RemoteNodeId, Is.EqualTo(original.Node.Id));
                Assert.That(remoteDirectories.Creates, Is.Empty);
                Assert.That(remoteDirectories.Deletes, Is.Empty);
                Assert.That(remoteDirectories.Renames, Is.EqualTo(new[] { (original.Node.Id, targetPath) }));
                Assert.That(result.RequiresUserAction, Is.False);
            });
            Assert.That(await stateStore.GetAsync("pair-a", sourcePath), Is.Null);
        }
    }
}
