// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task RefreshPlaceholder_WithOpenReaderPreservesIdentityAndRecoversAfterClose()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-contention-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            Guid rootId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            DesktopCloudFilesPlaceholderWriter writer = new(cloudFilesAdapter: adapter);
            Directory.CreateDirectory(root);
            NodeFileManifestDto remote = new()
            {
                Id = Guid.NewGuid(),
                NodeId = rootId,
                FileManifestId = Guid.NewGuid(),
                Name = "empty.txt",
                SizeBytes = 0,
                ContentHash = Convert.ToHexStringLower(SHA256.HashData([])),
                ETag = "original",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            };
            RemoteFilePlaceholderRequest request = new(pairId.ToString("D"), root, rootId, remote.Name, remote);
            string path = Path.Combine(root, remote.Name);
            try
            {
                using WindowsCloudFilesConnection connection = adapter.ConnectSyncRoot(
                    new SyncPairSettings
                    {
                        Id = pairId, LocalRootPath = root, RemoteRootNodeId = rootId,
                        Mode = SyncPairMode.WindowsVirtualFiles, IsEnabled = true,
                    }, new NoopWindowsCloudFilesCallbackHandler());
                RemoteFilePlaceholderResult original = await writer.CreatePlaceholderAsync(request);
                remote.FileManifestId = Guid.NewGuid();
                remote.ETag = "updated";
                remote.UpdatedAt = DateTime.UtcNow;

                using (FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    LocalFileUnavailableException? unavailable = Assert.ThrowsAsync<LocalFileUnavailableException>(
                        () => writer.CreatePlaceholderAsync(request));
                    Assert.Multiple(() =>
                    {
                        Assert.That(unavailable?.RequiresExclusiveAccess, Is.True);
                        Assert.That(unavailable?.FullPath, Is.EqualTo(path));
                        Assert.That(nativeApi.GetPlaceholderIdentity(path), Is.EqualTo(original.PlaceholderIdentity));
                        Assert.That(reader.Length, Is.Zero);
                    });
                }

                await writer.CreatePlaceholderAsync(request);
                WindowsCloudFilesPlaceholderIdentity updated = WindowsCloudFilesPlaceholderIdentity.Parse(
                    nativeApi.GetPlaceholderIdentity(path));
                Assert.Multiple(() =>
                {
                    Assert.That(updated.FileManifestId, Is.EqualTo(remote.FileManifestId));
                    Assert.That(updated.SyncPairId, Is.EqualTo(pairId));
                    Assert.That(new FileInfo(path).Length, Is.Zero);
                    Assert.That(nativeApi.GetPlaceholderState(path).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                });
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pairId, root);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
