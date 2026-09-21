// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        [Platform(Include = "Win")]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task RunOnceAsync_PinnedRegularFileRegainsCloudIdentity(bool startupRecovery)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-finalization-" + Guid.NewGuid().ToString("N"));
            string directoryPath = Path.Combine(root, "Pictures");
            string filePath = Path.Combine(directoryPath, "photo.jpg");
            byte[] content = RandomNumberGenerator.GetBytes(65539);
            string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
            SyncPairSettings pair = CreateVirtualFilesPair();
            pair.Id = Guid.NewGuid();
            pair.LocalRootPath = root;
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(WindowsCloudFilesNativeApiIntegrationTests.GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            Directory.CreateDirectory(root);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                pair.Id, root, "finalization-integration-test",
                Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root, WindowsCloudFilesAdapter.ProviderName, "finalization-integration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pair.Id, pair.RemoteRootNodeId)));
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler()));
                nativeApi.CreatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                    root, "Pictures", [1], 0, DateTime.UtcNow, DateTime.UtcNow, IsDirectory: true));
                await File.WriteAllBytesAsync(filePath, content);
                nativeApi.SetPinState(directoryPath, WindowsCloudFilesPinState.Pinned);
                nativeApi.SetPinState(filePath, WindowsCloudFilesPinState.Pinned);
                Assert.That((int)File.GetAttributes(filePath), Is.EqualTo(524320));
                Assert.That(await ReadStorageProviderStateAsync(filePath), Is.EqualTo("4"));
                FakeSyncStateStore stateStore = new();
                stateStore.UpsertEntry(CreateDirectoryState(pair, "Pictures"));
                SyncStateEntry state = CreatePlaceholderState(pair, "Pictures/photo.jpg");
                state.RemoteFileManifestId = Guid.NewGuid();
                state.RemoteContentHash = contentHash;
                state.RemoteSizeBytes = content.Length;
                if (startupRecovery)
                {
                    state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
                }
                stateStore.UpsertEntry(state);
                RecordingSyncPairWork inner = new();
                WindowsVirtualFilesDehydrationPairWork work = new(inner, stateStore, adapter);
                SyncRunRequest request = startupRecovery
                    ? SyncRunRequest.ForFull(SyncRunCause.Periodic)
                    : SyncRunRequest.ForLocalChangedPaths(["Pictures"]);

                await work.RunOnceAsync(pair, request);

                WindowsCloudFilesPlaceholderState nativeState = nativeApi.GetPlaceholderState(filePath);
                Assert.That(nativeState.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder), Is.True, "Cloud identity was not restored.");
                Assert.That(nativeState.HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                Assert.That(await ReadStorageProviderStateAsync(filePath), Is.EqualTo("3"), "Explorer must report pinned availability rather than sync pending.");
                WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(nativeApi.GetPlaceholderIdentity(filePath));
                Assert.Multiple(() =>
                {
                    Assert.That(identity.NodeFileId, Is.EqualTo(state.RemoteFileId));
                    Assert.That(identity.FileManifestId, Is.EqualTo(state.RemoteFileManifestId));
                    Assert.That(identity.ContentHash, Is.EqualTo(contentHash));
                    Assert.That(File.ReadAllBytes(filePath), Is.EqualTo(content));
                    Assert.That(File.GetAttributes(filePath).HasFlag((FileAttributes)FileAttributePinned), Is.True);
                    Assert.That(inner.Requests.Count, Is.EqualTo(startupRecovery ? 1 : 0));
                });
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pair.Id, root);
                Directory.Delete(root, recursive: true);
            }
        }

        private static async Task<string> ReadStorageProviderStateAsync(string filePath)
        {
            string status = await DesktopPowerShellFileReader.ReadAsync(
                "$target=$env:COTTON_SYNC_EXTERNAL_READ_PATH; "
                + "$shell=New-Object -ComObject Shell.Application; "
                + "$folder=$shell.Namespace([IO.Path]::GetDirectoryName($target)); "
                + "$item=$folder.ParseName([IO.Path]::GetFileName($target)); "
                + "$item.ExtendedProperty('System.StorageProviderState')",
                filePath, TimeSpan.FromSeconds(10), CancellationToken.None);
            return status.Trim();
        }
    }
}
