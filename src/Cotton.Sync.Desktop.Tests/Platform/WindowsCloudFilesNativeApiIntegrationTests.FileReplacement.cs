// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [TestCase("Replacement file contents", true, false)]
        [TestCase("Replacement content with a different length", true, false)]
        [TestCase("Short replacement", true, false)]
        [TestCase("", true, false)]
        [TestCase("", false, false)]
        [TestCase("", true, true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task UpdatePlaceholder_ExternalReaderSeesOnlyNewVersion(
            string updatedContent, bool hydrateOriginal, bool userEdited)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-version-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            string path = Path.Combine(root, "example.txt");
            byte[] original = Encoding.UTF8.GetBytes("Original hydrated content");
            byte[] updated = Encoding.UTF8.GetBytes(updatedContent);
            byte[] repopulated = Encoding.UTF8.GetBytes("Content following an empty version");
            string originalHash = Convert.ToHexStringLower(SHA256.HashData(original));
            string updatedHash = Convert.ToHexStringLower(SHA256.HashData(updated));
            string repopulatedHash = Convert.ToHexStringLower(SHA256.HashData(repopulated));
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                pairId, root, "version-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            Guid remoteRoot = Guid.NewGuid();
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root, WindowsCloudFilesAdapter.ProviderName, "version-test", WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, remoteRoot)));
            VersionedHydrationContentProvider provider = new(new Dictionary<string, byte[]>
            {
                [originalHash] = original,
                [updatedHash] = updated,
                [repopulatedHash] = repopulated,
            });
            WindowsCloudFilesHydrationCoordinator handler = new(provider, nativeApi);
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(new(root, handler));
                DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
                WindowsCloudFilesPlaceholderIdentity identity = new(
                    1, WindowsCloudFilesAdapter.ProviderId, pairId, remoteRoot, "example.txt",
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, original.Length, originalHash, null, timestamp);
                nativeApi.CreatePlaceholder(new(root, "example.txt", identity.ToBytes(), original.Length, timestamp, timestamp));
                if (hydrateOriginal)
                {
                    Assert.That((await ReadExternallyAsync(path)).Trim().ToLowerInvariant(), Is.EqualTo(originalHash));
                }
                nativeApi.SetInSyncState(path);
                WindowsCloudFilesPlaceholderIdentity nextIdentity = identity with
                {
                    ContentHash = updatedHash,
                    SizeBytes = updated.Length,
                    FileManifestId = Guid.NewGuid(),
                };
                if (userEdited)
                {
                    const string editedContent = "Content edited by the user";
                    await using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(editedContent));
                    }
                    Assert.Throws<WindowsCloudFilesNativeException>(() => nativeApi.UpdatePlaceholder(
                        new(root, "example.txt", nextIdentity.ToBytes(), updated.Length, timestamp, DateTime.UtcNow)));
                    Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(editedContent));
                    Assert.That(nativeApi.GetPlaceholderIdentity(path), Is.EqualTo(identity.ToBytes()));
                    return;
                }

                nativeApi.UpdatePlaceholder(new(root, "example.txt", nextIdentity.ToBytes(), updated.Length, timestamp, DateTime.UtcNow));

                Assert.That(new FileInfo(path).Length, Is.EqualTo(updated.Length));
                Assert.That((await ReadExternallyAsync(path)).Trim().ToLowerInvariant(), Is.EqualTo(updatedHash));
                Assert.That(nativeApi.GetPlaceholderIdentity(path), Is.EqualTo(nextIdentity.ToBytes()));
                if (updated.Length == 0)
                {
                    WindowsCloudFilesPlaceholderIdentity repopulatedIdentity = nextIdentity with
                    {
                        ContentHash = repopulatedHash,
                        SizeBytes = repopulated.Length,
                        FileManifestId = Guid.NewGuid(),
                    };
                    nativeApi.UpdatePlaceholder(new(root, "example.txt", repopulatedIdentity.ToBytes(),
                        repopulated.Length, timestamp, DateTime.UtcNow));
                    Assert.That(new FileInfo(path).Length, Is.EqualTo(repopulated.Length));
                    Assert.That((await ReadExternallyAsync(path)).Trim().ToLowerInvariant(), Is.EqualTo(repopulatedHash));
                    Assert.That(nativeApi.GetPlaceholderIdentity(path), Is.EqualTo(repopulatedIdentity.ToBytes()));
                }
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pairId, root);
                Directory.Delete(root, recursive: true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task ReplaceHydratedPlaceholder_ExternalReaderSeesExactNewContent(bool pinned)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-replacement-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            string path = Path.Combine(root, "example.txt");
            byte[] original = Encoding.UTF8.GetBytes("Original hydrated content");
            byte[] updated = Encoding.UTF8.GetBytes("Replacement content with a different length after rename");
            string originalHash = Convert.ToHexStringLower(SHA256.HashData(original));
            string updatedHash = Convert.ToHexString(SHA256.HashData(updated));
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                pairId, root, "replacement-test", Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root, WindowsCloudFilesAdapter.ProviderName, "replacement-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, Guid.NewGuid())));
            try
            {
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler()));
                await File.WriteAllBytesAsync(path, original);
                nativeApi.ConvertToPlaceholder(path, Encoding.UTF8.GetBytes("original-version"), false, true);
                nativeApi.SetPinState(path, pinned ? WindowsCloudFilesPinState.Pinned : WindowsCloudFilesPinState.Unpinned);
                AtomicLocalFileSyncWriter writer = new();

                LocalFileWriteResult result = await writer.WriteFileAsync(root, "example.txt",
                    (stream, token) => stream.WriteAsync(updated, token).AsTask(),
                    DateTime.UtcNow, originalHash);

                Assert.Multiple(() =>
                {
                    Assert.That(result.ConflictRelativePath, Is.Null);
                    Assert.That(new FileInfo(path).Length, Is.EqualTo(updated.Length));
                });
                Assert.That((await ReadExternallyAsync(path)).Trim(), Is.EqualTo(updatedHash));
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
