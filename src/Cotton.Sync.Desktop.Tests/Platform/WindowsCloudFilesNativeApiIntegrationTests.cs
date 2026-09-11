// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.App.SyncPairs;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task FinalizeUploadedFile_AllowsAnEditorToKeepTheFileOpen(bool existingPlaceholder)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "cotton-cloud-files-open-editor-" + Guid.NewGuid().ToString("N"));
            string fileName = "Debts.xlsx";
            string filePath = Path.Combine(root, fileName);
            byte[] content = Encoding.UTF8.GetBytes("updated workbook content");
            byte[] oldIdentity = Encoding.UTF8.GetBytes("old-version");
            byte[] updatedIdentity = Encoding.UTF8.GetBytes("updated-version");
            WindowsCloudFilesNativeApi nativeApi = new();
            Guid syncPairId = Guid.NewGuid();
            SyncPairSettings syncPair = new()
            {
                Id = syncPairId,
                DisplayName = "Open editor integration test",
                LocalRootPath = root,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/",
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
            };
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(filePath, content);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                syncPairId,
                root,
                "integration-test",
                Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root,
                WindowsCloudFilesAdapter.ProviderName,
                "integration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(
                    syncPairId,
                    syncPair.RemoteRootNodeId)));

            try
            {
                if (existingPlaceholder)
                {
                    nativeApi.ConvertToPlaceholder(filePath, oldIdentity, isDirectory: false, markInSync: false);
                }

                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                await using FileStream editor = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                WindowsCloudFilesUploadedFileFinalizationResult result = await nativeApi.FinalizeUploadedFileAsync(
                    new WindowsCloudFilesUploadedFileFinalizationRequest(
                        new WindowsCloudFilesNativePlaceholder(
                            root,
                            fileName,
                            updatedIdentity,
                            content.LongLength,
                            File.GetCreationTimeUtc(filePath),
                            lastWriteUtc),
                        Convert.ToHexStringLower(SHA256.HashData(content)),
                        content.LongLength,
                        lastWriteUtc,
                        existingPlaceholder
                            ? WindowsCloudFilesUploadedFileFinalizationMode.UpdateExistingPlaceholder
                            : WindowsCloudFilesUploadedFileFinalizationMode.ConvertRegularFile));

                byte[] actualIdentity = nativeApi.GetPlaceholderIdentity(filePath);
                editor.Position = 0;
                byte[] actualContent = new byte[content.Length];
                int bytesRead = await editor.ReadAsync(actualContent);
                Assert.Multiple(() =>
                {
                    Assert.That(result.IsFinalized, Is.True);
                    Assert.That(actualIdentity, Is.EqualTo(updatedIdentity));
                    Assert.That(bytesRead, Is.EqualTo(content.Length));
                    Assert.That(actualContent, Is.EqualTo(content));
                    Assert.That(editor.CanRead, Is.True);
                    Assert.That(editor.CanWrite, Is.True);
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
