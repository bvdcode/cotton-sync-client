// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [TestCase(false, false, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, false, false, true)]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task RefreshPlaceholder_AfterRemoteFileReplacementPreservesAvailabilityAndLocalEdits(
            bool hydrated, bool pinned, bool sameContent, bool locallyEdited)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-reuploaded-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            Guid rootId = Guid.NewGuid();
            string path = Path.Combine(root, "report.zip");
            byte[] original = Encoding.UTF8.GetBytes("Original remote contents");
            byte[] replacement = sameContent ? original : Encoding.UTF8.GetBytes("Reuploaded contents with a new file identifier");
            string originalHash = Convert.ToHexStringLower(SHA256.HashData(original));
            string replacementHash = Convert.ToHexStringLower(SHA256.HashData(replacement));
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            VersionedHydrationContentProvider provider = new(new Dictionary<string, byte[]>
            {
                [originalHash] = original,
                [replacementHash] = replacement,
            });
            WindowsCloudFilesHydrationCoordinator handler = new(provider, nativeApi);
            NodeFileManifestDto remote = new()
            {
                Id = Guid.NewGuid(), NodeId = rootId, FileManifestId = Guid.NewGuid(), Name = "report.zip",
                ContentHash = originalHash, SizeBytes = original.Length, ETag = "original",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1), UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            };
            RemoteFilePlaceholderRequest request = new(pairId.ToString("D"), root, rootId, remote.Name, remote);
            Directory.CreateDirectory(root);
            try
            {
                using WindowsCloudFilesConnection connection = adapter.ConnectSyncRoot(
                    new SyncPairSettings
                    {
                        Id = pairId, LocalRootPath = root, RemoteRootNodeId = rootId,
                        Mode = SyncPairMode.WindowsVirtualFiles, IsEnabled = true,
                    }, handler);
                RemoteFilePlaceholderResult previous = adapter.CreateFilePlaceholder(request);
                if (hydrated)
                {
                    Assert.That((await ReadExternallyAsync(path)).Trim().ToLowerInvariant(), Is.EqualTo(originalHash));
                }

                nativeApi.SetInSyncState(path);
                if (pinned)
                {
                    nativeApi.SetPinState(path, WindowsCloudFilesPinState.Pinned);
                }

                NodeFileManifestDto reuploaded = new()
                {
                    Id = Guid.NewGuid(), NodeId = rootId, FileManifestId = Guid.NewGuid(), Name = remote.Name,
                    ContentHash = replacementHash, SizeBytes = replacement.Length, ETag = "replacement",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                request = request with
                {
                    RemoteFile = reuploaded,
                    ExistingHydrationState = hydrated ? SyncPlaceholderHydrationState.Hydrated : SyncPlaceholderHydrationState.RemoteOnly,
                };
                if (locallyEdited)
                {
                    const string localContent = "Local changes must survive remote replacement";
                    await using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(localContent));
                    }

                    Assert.That(nativeApi.GetPlaceholderIdentity(path), Is.EqualTo(previous.PlaceholderIdentity));
                    WindowsCloudFilesNativeException? exception = Assert.Throws<WindowsCloudFilesNativeException>(
                        () => adapter.CreateFilePlaceholder(request));
                    Assert.That(exception!.Operation, Is.EqualTo("CfUpdatePlaceholder"));
                    Assert.Multiple(() =>
                    {
                        Assert.That(File.ReadAllText(path), Is.EqualTo(localContent));
                        Assert.That(nativeApi.GetPlaceholderIdentity(path), Is.EqualTo(previous.PlaceholderIdentity));
                    });
                    return;
                }

                RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);

                Assert.Multiple(() =>
                {
                    Assert.That(WindowsCloudFilesPlaceholderIdentity.Parse(nativeApi.GetPlaceholderIdentity(path)).NodeFileId,
                        Is.EqualTo(reuploaded.Id));
                    Assert.That(result.HydrationState, Is.EqualTo(hydrated
                        ? SyncPlaceholderHydrationState.Hydrated : SyncPlaceholderHydrationState.RemoteOnly));
                    Assert.That(((int)File.GetAttributes(path) & 0x00080000) != 0, Is.EqualTo(pinned));
                    Assert.That(nativeApi.GetPlaceholderState(path).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                });
                Assert.That((await ReadExternallyAsync(path)).Trim().ToLowerInvariant(), Is.EqualTo(replacementHash));
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
