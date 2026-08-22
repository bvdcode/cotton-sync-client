// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;
using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Chunks;
using Cotton.Sdk.Files;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Notifications;
using Cotton.Sdk.Realtime;
using Cotton.Sdk.Settings;
using Cotton.Sdk.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        [Test]
        public async Task DownloadFileAsync_And_DeleteFileAsync_DelegateToSdkFileClient()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 8);
            client.FilesClient.Downloads[fileId] = Encoding.UTF8.GetBytes("downloaded");
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);
            await using MemoryStream destination = new MemoryStream();

            await synchronizer.DownloadFileAsync(fileId, destination);
            await synchronizer.DeleteFileAsync(fileId, skipTrash: true, expectedETag: "sha256-current");

            Assert.Multiple(() =>
            {
                Assert.That(Encoding.UTF8.GetString(destination.ToArray()), Is.EqualTo("downloaded"));
                Assert.That(client.FilesClient.Deletes, Is.EqualTo(new[] { (fileId, true, "sha256-current") }));
            });
        }

        [Test]
        public async Task DownloadFileAsync_ReportsSdkDownloadProgress()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 8);
            client.FilesClient.Downloads[fileId] = Encoding.UTF8.GetBytes("downloaded");
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);
            await using MemoryStream destination = new MemoryStream();
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();

            await synchronizer.DownloadFileAsync(
                fileId,
                "Docs/file.txt",
                totalBytes: 10,
                destination,
                progress);

            Assert.Multiple(() =>
            {
                Assert.That(progress.Values.Select(value => value.TransferredBytes), Is.EqualTo(new long[] { 0, 10, 10 }));
                Assert.That(progress.Values.Select(value => value.TotalBytes), Is.All.EqualTo(10));
                Assert.That(progress.Values.Select(value => value.Direction), Is.All.EqualTo(SyncTransferDirection.Download));
                Assert.That(progress.Values.Select(value => value.RelativePath), Is.All.EqualTo("Docs/file.txt"));
                Assert.That(progress.Values[^1].IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task DownloadFileRangeAsync_DelegatesToSdkRangeApiWithETagAndProgress()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 8);
            client.FilesClient.Downloads[fileId] = Encoding.UTF8.GetBytes("0123456789abcdef");
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);
            await using MemoryStream destination = new MemoryStream();
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();

            await synchronizer.DownloadFileRangeAsync(
                fileId,
                "Docs/file.txt",
                offset: 4,
                length: 6,
                expectedETag: "sha256-current",
                destination,
                progress);

            Assert.Multiple(() =>
            {
                Assert.That(Encoding.UTF8.GetString(destination.ToArray()), Is.EqualTo("456789"));
                Assert.That(client.FilesClient.RangeDownloads, Is.EqualTo(new[] { (fileId, 4L, 6L, "sha256-current") }));
                Assert.That(progress.Values.Select(value => value.TransferredBytes), Is.EqualTo(new long[] { 0, 6, 6 }));
                Assert.That(progress.Values.Select(value => value.TotalBytes), Is.All.EqualTo(6));
                Assert.That(progress.Values.Select(value => value.Direction), Is.All.EqualTo(SyncTransferDirection.Download));
                Assert.That(progress.Values.Select(value => value.RelativePath), Is.All.EqualTo("Docs/file.txt"));
                Assert.That(progress.Values[^1].IsCompleted, Is.True);
            });
        }

        private LocalFileSnapshot WriteLocalFile(string relativePath, byte[] bytes)
        {
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            File.SetLastWriteTimeUtc(fullPath, new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc));
            return new LocalFileSnapshot
            {
                RelativePath = relativePath,
                FullPath = fullPath,
                ContentHash = Hash(bytes),
                SizeBytes = bytes.Length,
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
            };
        }

        private NodeFileManifestDto RemoteFile(string name, string contentHash)
        {
            return new NodeFileManifestDto
            {
                Id = Guid.NewGuid(),
                NodeId = _rootNodeId,
                FileManifestId = Guid.NewGuid(),
                OriginalNodeFileId = Guid.NewGuid(),
                OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = name,
                ContentType = "application/octet-stream",
                ContentHash = contentHash,
                ETag = "sha256-" + contentHash,
            };
        }

        private static NodeDto Node(Guid id, Guid parentId, string name)
        {
            return new NodeDto
            {
                Id = id,
                ParentId = parentId,
                LayoutId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Name = name,
            };
        }

        private static string HashText(string text)
        {
            return Hash(Encoding.UTF8.GetBytes(text));
        }

        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

    }
}
