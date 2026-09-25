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
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
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
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new MemoryStream();
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();

            await synchronizer.DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, 10, ETag(client.FilesClient.Downloads[fileId])),
                "Docs/file.txt",
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
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
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

        [Test]
        public async Task DownloadFileAsync_AfterInterruption_ReusesCompletedChunksAcrossInstances()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijkl");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.DownloadChunkSizeBytes = 4;
            client.FilesClient.InterruptedChunkNumber = 2;
            client.FilesClient.InterruptedChunkFailuresRemaining = 1;
            SdkRemoteFileSynchronizerOptions options = new()
            {
                DownloadCacheDirectory = Path.Combine(_root, "cache"),
                MaxDownloadChunkAttempts = 1,
                MaxConcurrentChunkDownloads = 1,
            };

            await using (MemoryStream interrupted = new())
            {
                Assert.ThrowsAsync<HttpIOException>(async () =>
                    await new SdkRemoteFileSynchronizer(client, options)
                        .DownloadFileAsync(new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                            "Docs/file.txt", interrupted, null));
            }

            await using MemoryStream resumed = new();
            await new SdkRemoteFileSynchronizer(client, options).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)), "Docs/file.txt", resumed, null);

            Assert.Multiple(() =>
            {
                Assert.That(resumed.ToArray(), Is.EqualTo(content));
                Assert.That(
                    client.FilesClient.ChunkDownloads.Select(item => item.ChunkNumber),
                    Is.EqualTo(new[] { 0, 1, 2, 0, 2 }));
                Assert.That(Directory.EnumerateDirectories(options.DownloadCacheDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task DownloadFileAsync_TruncatedCachedChunk_RejectsIncompleteFile()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijkl");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.DownloadChunkSizeBytes = 4;
            client.FilesClient.InterruptedChunkNumber = 2;
            client.FilesClient.InterruptedChunkFailuresRemaining = 1;
            SdkRemoteFileSynchronizerOptions options = new()
            {
                DownloadCacheDirectory = Path.Combine(_root, "cache"),
                MaxDownloadChunkAttempts = 1,
                MaxConcurrentChunkDownloads = 1,
            };

            await using (MemoryStream interrupted = new())
            {
                Assert.ThrowsAsync<HttpIOException>(async () =>
                    await new SdkRemoteFileSynchronizer(client, options)
                        .DownloadFileAsync(new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                            "Docs/file.txt", interrupted, null));
            }

            string cachedChunk = Directory.EnumerateFiles(
                options.DownloadCacheDirectory, "00000001.chunk", SearchOption.AllDirectories).Single();
            await File.WriteAllBytesAsync(cachedChunk, Encoding.UTF8.GetBytes("x"));
            await using MemoryStream resumed = new();
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await new SdkRemoteFileSynchronizer(client, options).DownloadFileAsync(
                    new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                    "Docs/file.txt", resumed, null));

            Assert.Multiple(() =>
            {
                Assert.That(resumed.Length, Is.Zero);
                Assert.That(Directory.EnumerateDirectories(options.DownloadCacheDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task DownloadFileRangeAsync_RequestsOnlyTheNeededBytes()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = Encoding.UTF8.GetBytes("abcdefghijklmnop");
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new();

            await synchronizer.DownloadFileRangeAsync(
                fileId, "Docs/file.txt", offset: 5, length: 7,
                expectedETag: "sha256-current", destination, transferProgress: null);

            Assert.Multiple(() =>
            {
                Assert.That(Encoding.UTF8.GetString(destination.ToArray()), Is.EqualTo("fghijkl"));
                Assert.That(client.FilesClient.RangeDownloads.Select(item => (item.Offset, item.Length)),
                    Is.EqualTo(new[] { (5L, 7L) }));
            });
        }

        [Test]
        public async Task DownloadFileAsync_LargeChunk_UsesOneChunkRequest()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            byte[] content = new byte[10 * 1024 * 1024];
            client.FilesClient.Downloads[fileId] = content;
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new();

            await synchronizer.DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                "Docs/large.bin", destination, null);

            Assert.Multiple(() =>
            {
                Assert.That(destination.Length, Is.EqualTo(content.Length));
                Assert.That(client.FilesClient.ChunkDownloads.Select(item => item.ChunkNumber),
                    Is.EqualTo(new[] { 0 }));
            });
        }

        [Test]
        public async Task DownloadFileAsync_EmptyFile_CompletesWithoutContentRequest()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = [];
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new();

            await synchronizer.DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, 0, ETag([])),
                "Docs/empty.txt", destination, null);

            Assert.Multiple(() =>
            {
                Assert.That(destination.Length, Is.Zero);
                Assert.That(client.FilesClient.ChunkDownloads, Is.Empty);
            });
        }

        [Test]
        public async Task DownloadFileAsync_InterruptedChunk_RetriesOnlyThatChunk()
        {
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            byte[] content = new byte[10 * 1024 * 1024];
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.DownloadChunkSizeBytes = 4 * 1024 * 1024;
            client.FilesClient.InterruptedChunkNumber = 1;
            client.FilesClient.InterruptedChunkFailuresRemaining = 1;
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new();

            await synchronizer.DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                "Docs/large.bin", destination, null);

            Assert.Multiple(() =>
            {
                Assert.That(destination.Length, Is.EqualTo(content.Length));
                Assert.That(client.FilesClient.ChunkDownloads.Count(item => item.ChunkNumber == 0), Is.EqualTo(1));
                Assert.That(client.FilesClient.ChunkDownloads.Count(item => item.ChunkNumber == 1), Is.EqualTo(2));
                Assert.That(client.FilesClient.ChunkDownloads.Count(item => item.ChunkNumber == 2), Is.EqualTo(1));
            });
        }

        [Test]
        public async Task DownloadFileAsync_DownloadsFourRemainingChunksConcurrently()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwx");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.DownloadChunkSizeBytes = 4;
            client.FilesClient.ChunkDownloadDelay = TimeSpan.FromMilliseconds(50);
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new();

            await synchronizer.DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                "Docs/file.txt", destination, null);

            Assert.Multiple(() =>
            {
                Assert.That(destination.ToArray(), Is.EqualTo(content));
                Assert.That(client.FilesClient.MaxActiveChunkDownloads, Is.EqualTo(4));
            });
        }

        [Test]
        public async Task DownloadFileAsync_RemovesExpiredInterruptedDownloadCache()
        {
            string cacheDirectory = Path.Combine(_root, "cache");
            string expiredDirectory = Path.Combine(
                cacheDirectory, Guid.NewGuid().ToString("N") + "-" + new string('a', 64));
            Directory.CreateDirectory(expiredDirectory);
            await File.WriteAllTextAsync(Path.Combine(expiredDirectory, "00000000.chunk"), "old");
            Directory.SetLastWriteTimeUtc(expiredDirectory, DateTime.UtcNow.AddDays(-2));

            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("current");
            FakeCottonCloudClient client = new(chunkSizeBytes: 8);
            client.FilesClient.Downloads[fileId] = content;
            SdkRemoteFileSynchronizer synchronizer = CreateDownloader(client);
            await using MemoryStream destination = new();

            await synchronizer.DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                "Docs/file.txt", destination, null);

            Assert.Multiple(() =>
            {
                Assert.That(destination.ToArray(), Is.EqualTo(content));
                Assert.That(Directory.Exists(expiredDirectory), Is.False);
            });
        }

        private SdkRemoteFileSynchronizer CreateDownloader(FakeCottonCloudClient client)
        {
            return new SdkRemoteFileSynchronizer(client, new SdkRemoteFileSynchronizerOptions
            {
                DownloadCacheDirectory = Path.Combine(_root, "cache"),
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

        private static string ETag(byte[] bytes)
        {
            return "sha256-" + Hash(bytes);
        }

    }
}
