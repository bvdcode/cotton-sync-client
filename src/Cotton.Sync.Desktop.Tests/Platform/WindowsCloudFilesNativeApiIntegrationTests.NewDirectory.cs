// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task SyncNewDirectory_ThenRenamePreservesCloudFilesIdentity(bool fullReconcile)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-new-folder-" + Guid.NewGuid().ToString("N"));
            string statePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
            SyncPairSettings pair = new()
            {
                Id = Guid.NewGuid(), LocalRootPath = root, RemoteRootNodeId = Guid.NewGuid(),
                Mode = SyncPairMode.WindowsVirtualFiles, IsEnabled = true,
            };
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            SqliteSyncStateStore stateStore = new(statePath);
            await stateStore.InitializeAsync();
            DirectoryRenameRemoteFixture remote = new(new NodeDto { Id = pair.RemoteRootNodeId, Name = "Root" });
            SyncEngine engine = new(new LocalFileScanner(), remote, remote, stateStore, remoteDirectories: remote);
            SyncPair syncPair = new()
            {
                SyncPairId = pair.Id.ToString("D"), LocalRootPath = root, RemoteRootNodeId = pair.RemoteRootNodeId,
                MaterializationMode = SyncPairMaterializationMode.WindowsVirtualFiles,
            };
            Directory.CreateDirectory(root);
            try
            {
                using WindowsCloudFilesConnection connection = adapter.ConnectSyncRoot(pair,
                    new NoopWindowsCloudFilesCallbackHandler());
                Directory.CreateDirectory(Path.Combine(root, "Backups", "New folder"));
                await engine.RunOnceAsync(syncPair);
                foreach (string path in new[] { "Backups/New folder", "Backups" })
                {
                    SyncStateEntry state = (await stateStore.GetAsync(pair.Id.ToString("D"), path))!;
                    adapter.CreateDirectoryPlaceholder(WindowsCloudFilesDirectoryRequestFactory.Create(pair, state, path));
                }

                string oldPath = "Backups/New folder";
                string newPath = "Backups/Reports";
                string target = Path.Combine(root, "Backups", "Reports");
                WindowsCloudFilesDirectoryPlaceholderIdentity original = WindowsCloudFilesDirectoryPlaceholderIdentity.Parse(
                    nativeApi.GetPlaceholderIdentity(Path.Combine(root, "Backups", "New folder")));
                Directory.Move(Path.Combine(root, "Backups", "New folder"), target);
                LocalPathRename[] renames = [new(oldPath, newPath)];
                SyncRunOptions options = new()
                {
                    Scope = fullReconcile ? SyncRunScope.ForFull(renames)
                        : SyncRunScope.ForLocalChangedPaths([oldPath, newPath], [], renames),
                };

                SyncRunResult result = await engine.RunOnceAsync(syncPair, options);
                SyncStateEntry renamedState = (await stateStore.GetAsync(pair.Id.ToString("D"), newPath))!;
                adapter.CreateDirectoryPlaceholder(WindowsCloudFilesDirectoryRequestFactory.Create(pair, renamedState, newPath));
                WindowsCloudFilesDirectoryPlaceholderIdentity updated = WindowsCloudFilesDirectoryPlaceholderIdentity.Parse(
                    nativeApi.GetPlaceholderIdentity(target));
                await engine.RunOnceAsync(syncPair);
                Assert.Multiple(() =>
                {
                    Assert.That(renamedState.RemoteNodeId, Is.EqualTo(original.NodeId));
                    Assert.That(updated.NodeId, Is.EqualTo(original.NodeId));
                    Assert.That(Directory.Exists(target), Is.True);
                    Assert.That(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint), Is.True);
                    Assert.That(remote.Creates, Is.EqualTo(2));
                    Assert.That(remote.Deletes, Is.Zero);
                    Assert.That(remote.Renames, Is.EqualTo(1));
                    Assert.That(result.RequiresUserAction, Is.False);
                });
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pair.Id, root);
                Directory.Delete(root, recursive: true);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Delete(statePath);
                File.Delete(statePath + "-wal");
                File.Delete(statePath + "-shm");
            }
        }
    }
}
