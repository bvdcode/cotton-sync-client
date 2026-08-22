// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        private static uint InvokeNativeFlagFactory(string methodName, bool isDirectory)
        {
            System.Reflection.MethodInfo? method = typeof(WindowsCloudFilesNativeApi).GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            object? result = method!.Invoke(null, [isDirectory]);
            Assert.That(result, Is.Not.Null);
            return Convert.ToUInt32(result);
        }

        private WindowsVirtualFilesRootSafetyPolicy CreatePolicy()
        {
            return new WindowsVirtualFilesRootSafetyPolicy(
                _ => string.Empty,
                () => _tempDirectory);
        }

        private static SyncPairSettings CreateSyncPair(string root)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Windows virtual files",
                LocalRootPath = root,
                RemoteDisplayPath = "/",
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
            };
        }

        private static RemoteFilePlaceholderRequest CreateRequest(
            string localRootPath,
            string relativePath,
            string syncPairId = "11111111-1111-1111-1111-111111111111")
        {
            return new RemoteFilePlaceholderRequest(
                syncPairId,
                localRootPath,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = 12,
                    ContentHash = "hash",
                    ETag = "etag",
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private static void TrackExistingFilePlaceholder(
            FakeCloudFilesNativeApi nativeApi,
            string fullPath,
            RemoteFilePlaceholderRequest request)
        {
            nativeApi.PlaceholderIdentities[fullPath] = WindowsCloudFilesPlaceholderIdentity
                .Create(request, SyncPath.Normalize(request.RelativePath))
                .ToBytes();
        }

        private static SyncStateEntry CreateUploadedFileState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = "uploaded-hash",
                LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc),
                LocalSizeBytes = 16,
                RemoteSizeBytes = 16,
                RemoteNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                RemoteFileId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                RemoteFileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                RemoteOriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                RemoteContentHash = "uploaded-hash",
                RemoteETag = "uploaded-etag",
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 06, 30, DateTimeKind.Utc),
            };
        }

        private static RemoteDirectoryMaterializationRequest CreateDirectoryRequest(
            string localRootPath,
            string relativePath,
            string syncPairId = "11111111-1111-1111-1111-111111111111")
        {
            return new RemoteDirectoryMaterializationRequest(
                syncPairId,
                localRootPath,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                relativePath,
                new NodeDto
                {
                    Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Name = Path.GetFileName(relativePath),
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                });
        }

        private static void TrackExistingDirectoryPlaceholder(
            FakeCloudFilesNativeApi nativeApi,
            string fullPath,
            RemoteDirectoryMaterializationRequest request)
        {
            nativeApi.PlaceholderIdentities[fullPath] = WindowsCloudFilesDirectoryPlaceholderIdentity
                .Create(request, SyncPath.Normalize(request.RelativePath))
                .ToBytes();
        }
    }
}
