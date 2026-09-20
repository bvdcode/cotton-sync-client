// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public void UpdateDirectoryPlaceholder_AllowsAnActiveDirectoryWatcher()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-directory-" + Guid.NewGuid().ToString("N"));
            string directoryPath = Path.Combine(root, "Documents");
            Guid syncPairId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                syncPairId, root, "directory-integration-test",
                Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root, WindowsCloudFilesAdapter.ProviderName, "directory-integration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(syncPairId, Guid.NewGuid())));
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler()));
                byte[] oldIdentity = Encoding.UTF8.GetBytes("original-directory");
                byte[] updatedIdentity = Encoding.UTF8.GetBytes("updated-directory");
                DateTime createdAt = DateTime.UtcNow.AddMinutes(-1);
                nativeApi.CreatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                    root, "Documents", oldIdentity, 0, createdAt, createdAt, IsDirectory: true));
                nativeApi.SetPinState(directoryPath, WindowsCloudFilesPinState.Unpinned);
                using FileSystemWatcher watcher = new(directoryPath)
                {
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = true,
                };

                nativeApi.UpdatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                    root, "Documents", updatedIdentity, 0, createdAt, DateTime.UtcNow, IsDirectory: true));
                nativeApi.SetInSyncState(directoryPath);

                Assert.Multiple(() =>
                {
                    Assert.That(nativeApi.GetPlaceholderIdentity(directoryPath), Is.EqualTo(updatedIdentity));
                    Assert.That(nativeApi.GetPlaceholderState(directoryPath).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                    Assert.That(watcher.EnableRaisingEvents, Is.True);
                });
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(syncPairId, root);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
