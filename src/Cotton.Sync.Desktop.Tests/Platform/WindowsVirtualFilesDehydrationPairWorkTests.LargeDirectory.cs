// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [Test]
        [Platform(Include = "Win")]
        [Explicit("Registers a temporary Windows Cloud Files sync root with a large pinned folder.")]
        public async Task RunOnceAsync_LargePinnedFolderRemainsPendingUntilAllChildrenHydrate()
        {
            const int fileCount = 1500;
            const int availableFileCount = 1000;
            string root = Path.Combine(Path.GetTempPath(), "cotton-large-pinned-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "Photos");
            SyncPairSettings pair = CreateVirtualFilesPair();
            pair.Id = Guid.NewGuid();
            pair.LocalRootPath = root;
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(WindowsCloudFilesNativeApiIntegrationTests.GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            Directory.CreateDirectory(root);
            registrar.Register(new(pair.Id, root, "large-folder-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new(root, WindowsCloudFilesAdapter.ProviderName, "large-folder-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pair.Id, pair.RemoteRootNodeId)));
            byte[] content = RandomNumberGenerator.GetBytes(512);
            GatedHydrationContentProvider provider = new(content);
            WindowsCloudFilesHydrationCoordinator handler = new(provider, nativeApi);
            Task? run = null;
            WindowsCloudFilesConnection? connection = null;
            try
            {
                connection = nativeApi.ConnectSyncRoot(new(root, handler));
                DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
                nativeApi.CreatePlaceholder(new(root, "Photos", [1], 0, timestamp, timestamp, IsDirectory: true));
                nativeApi.SetPinState(directory, WindowsCloudFilesPinState.Pinned);
                FakeSyncStateStore stateStore = new();
                stateStore.UpsertEntry(CreateDirectoryState(pair, "Photos"));
                for (int index = 0; index < fileCount; index++)
                {
                    string name = $"photo-{index:D4}.bin";
                    string path = Path.Combine(directory, name);
                    SyncStateEntry state = CreatePlaceholderState(pair, "Photos/" + name);
                    state.RemoteFileId = Guid.NewGuid();
                    state.RemoteFileManifestId = Guid.NewGuid();
                    state.RemoteContentHash = Convert.ToHexStringLower(SHA256.HashData(content));
                    state.RemoteSizeBytes = content.Length;
                    WindowsCloudFilesPlaceholderIdentity identity = new(1, WindowsCloudFilesAdapter.ProviderId, pair.Id,
                        pair.RemoteRootNodeId, state.RelativePath, state.RemoteFileId.Value, state.RemoteNodeId!.Value,
                        state.RemoteFileManifestId.Value, null, content.Length, state.RemoteContentHash, null, timestamp);
                    state.PlaceholderIdentity = identity.ToBytes();
                    if (index < availableFileCount)
                    {
                        await File.WriteAllBytesAsync(path, content);
                        nativeApi.ConvertToPlaceholder(path, state.PlaceholderIdentity, isDirectory: false, markInSync: true);
                        state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
                        state.LocalContentHash = state.RemoteContentHash;
                        state.LocalSizeBytes = content.Length;
                        state.LocalLastWriteUtc = File.GetLastWriteTimeUtc(path);
                    }
                    else
                    {
                        nativeApi.CreatePlaceholder(new(directory, name, state.PlaceholderIdentity, content.Length, timestamp, timestamp));
                    }
                    nativeApi.SetPinState(path, WindowsCloudFilesPinState.Pinned);
                    stateStore.UpsertEntry(state);
                }
                nativeApi.SetInSyncState(directory);
                WindowsVirtualFilesDehydrationPairWork work = new(new RecordingSyncPairWork(), stateStore, adapter);
                run = Task.Run(() => work.RunOnceAsync(pair, SyncRunRequest.ForLocalChangedPaths(["Photos"])));
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Multiple(() =>
                {
                    Assert.That(nativeApi.GetPlaceholderState(directory).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.False);
                    Assert.That(run.IsCompleted, Is.False);
                });
                Assert.That(await ReadStorageProviderStateAsync(directory), Is.EqualTo("10"));
                provider.Release.TrySetResult();
                await run.WaitAsync(TimeSpan.FromMinutes(2));
                Assert.That(await ReadStorageProviderStateAsync(directory), Is.EqualTo("3"));
                Assert.That(stateStore.GetRequired(pair.Id, "Photos/photo-1499.bin").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                TestContext.Out.WriteLine($"Folder stayed pending while {fileCount - availableFileCount} files required hydration, then became available.");
            }
            finally
            {
                provider.Release.TrySetResult();
                try
                {
                    if (run is not null)
                    {
                        await run.WaitAsync(TimeSpan.FromMinutes(2));
                    }
                }
                finally
                {
                    connection?.Dispose();
                    nativeApi.UnregisterSyncRoot(root);
                    registrar.Unregister(pair.Id, root);
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }
}
