// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public void RefreshPlaceholders_AfterDirectoryMovePreservesOwnershipAndUpdatesPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-move-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            Guid rootId = Guid.NewGuid();
            DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            WindowsCloudFilesAdapter adapter = new(nativeApi: nativeApi, storageProviderRegistrar: registrar);
            Directory.CreateDirectory(root);
            RemoteDirectoryMaterializationRequest directoryRequest = new(
                pairId.ToString("D"), root, rootId, "Projects",
                new NodeDto { Id = Guid.NewGuid(), ParentId = rootId, Name = "Projects", CreatedAt = timestamp, UpdatedAt = timestamp });
            RemoteFilePlaceholderRequest fileRequest = new(
                pairId.ToString("D"), root, rootId, "Projects/report.txt",
                new NodeFileManifestDto
                {
                    Id = Guid.NewGuid(), NodeId = directoryRequest.RemoteDirectory.Id,
                    FileManifestId = Guid.NewGuid(), Name = "report.txt", SizeBytes = 12,
                    ContentHash = "remote-content", ETag = "remote-version", CreatedAt = timestamp, UpdatedAt = timestamp,
                });
            try
            {
                adapter.CreateDirectoryPlaceholder(directoryRequest);
                using WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler()));
                adapter.CreateFilePlaceholder(fileRequest);
                Directory.CreateDirectory(Path.Combine(root, "Archive"));
                Directory.Move(Path.Combine(root, "Projects"), Path.Combine(root, "Archive", "Projects"));
                string movedDirectory = Path.Combine(root, "Archive", "Projects");
                string movedFile = Path.Combine(movedDirectory, "report.txt");
                nativeApi.SetInSyncState(movedDirectory);

                adapter.CreateDirectoryPlaceholder(directoryRequest with { RelativePath = "Archive/Projects" });
                adapter.CreateFilePlaceholder(fileRequest with { RelativePath = "Archive/Projects/report.txt" });

                WindowsCloudFilesDirectoryPlaceholderIdentity directoryIdentity =
                    WindowsCloudFilesDirectoryPlaceholderIdentity.Parse(nativeApi.GetPlaceholderIdentity(movedDirectory));
                WindowsCloudFilesPlaceholderIdentity fileIdentity =
                    WindowsCloudFilesPlaceholderIdentity.Parse(nativeApi.GetPlaceholderIdentity(movedFile));
                Assert.Multiple(() =>
                {
                    Assert.That(directoryIdentity.RelativePath, Is.EqualTo("Archive/Projects"));
                    Assert.That(directoryIdentity.NodeId, Is.EqualTo(directoryRequest.RemoteDirectory.Id));
                    Assert.That(fileIdentity.RelativePath, Is.EqualTo("Archive/Projects/report.txt"));
                    Assert.That(fileIdentity.NodeFileId, Is.EqualTo(fileRequest.RemoteFile.Id));
                    Assert.That(fileIdentity.SyncPairId, Is.EqualTo(pairId));
                    Assert.That(new FileInfo(movedFile).Length, Is.EqualTo(12));
                    Assert.That(nativeApi.GetPlaceholderState(movedFile).HasFlag(WindowsCloudFilesPlaceholderState.InSync), Is.True);
                    Assert.That(Directory.Exists(Path.Combine(root, "Projects")), Is.False);
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
