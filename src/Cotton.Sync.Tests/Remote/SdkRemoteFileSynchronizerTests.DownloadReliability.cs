// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Text;
using Cotton.Sdk;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(32)]
        [TestCase(33)]
        public async Task DownloadFileAsync_ChunkBoundarySizesWriteExactFile(int length)
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Enumerable.Range(0, length).Select(index => (byte)(index * 7)).ToArray();
            FakeCottonCloudClient client = new(chunkSizeBytes: 8);
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.DownloadChunkSizeBytes = 8;
            string target = Path.Combine(_root, "boundary.bin");
            await using (FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write))
            {
                await CreateDownloader(client).DownloadFileAsync(
                    new RemoteFileDownloadIdentity(fileId, length, ETag(content)),
                    "boundary.bin", destination, null);
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(target), Is.EqualTo(content));
                Assert.That(client.FilesClient.ChunkDownloads.Select(item => item.ChunkNumber).Order(),
                    Is.EqualTo(Enumerable.Range(0, (length + 7) / 8)));
                Assert.That(client.FilesClient.ChunkDownloads.Select(item => item.ExpectedETag),
                    Is.All.EqualTo(ETag(content)));
                string cache = Path.Combine(_root, "cache");
                if (length == 0)
                {
                    Assert.That(Directory.Exists(cache), Is.False);
                }
                else
                {
                    Assert.That(Directory.EnumerateDirectories(cache), Is.Empty);
                }
            });
        }

        [Test]
        public async Task DownloadFileAsync_OutOfOrderChunkCompletionPreservesByteOrderAndConcurrencyLimit()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwx");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            TaskCompletionSource[] started = Enumerable.Range(0, 6)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            TaskCompletionSource[] released = Enumerable.Range(0, 6)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            TaskCompletionSource[] completed = Enumerable.Range(0, 6)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            client.FilesClient.OnChunkDownloadAsync = async (number, destination, progress, token) =>
            {
                started[number].TrySetResult();
                if (number > 0)
                {
                    await released[number].Task.WaitAsync(token);
                }
                await destination.WriteAsync(content.AsMemory(number * 4, 4), token);
                progress?.Report(4);
                completed[number].TrySetResult();
                return 6;
            };
            await using MemoryStream destination = new();
            Task download = CreateDownloader(client).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)), "ordered.bin", destination, null);
            try
            {
                await Task.WhenAll(started.Skip(1).Take(4).Select(signal => signal.Task))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Multiple(() =>
                {
                    Assert.That(started[5].Task.IsCompleted, Is.False);
                    Assert.That(client.FilesClient.MaxActiveChunkDownloads, Is.EqualTo(4));
                    Assert.That(destination.Length, Is.Zero);
                });
                foreach (int number in new[] { 4, 2, 3, 1, 5 })
                {
                    released[number].TrySetResult();
                    await completed[number].Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                foreach (TaskCompletionSource release in released)
                {
                    release.TrySetResult();
                }
                await download.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.That(destination.ToArray(), Is.EqualTo(content));
            Assert.That(client.FilesClient.MaxActiveChunkDownloads, Is.EqualTo(4));
        }

        [TestCase(-1)]
        [TestCase(1)]
        public async Task DownloadFileAsync_IncorrectChunkLengthDoesNotPublishFile(int lengthChange)
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijkl");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            client.FilesClient.OnChunkDownloadAsync = async (number, destination, _, token) =>
            {
                byte[] bytes = content.AsSpan(number * 4, 4).ToArray();
                if (number == 1)
                {
                    Array.Resize(ref bytes, bytes.Length + lengthChange);
                }
                await destination.WriteAsync(bytes, token);
                return 3;
            };
            await using MemoryStream destination = new();

            Assert.ThrowsAsync<InvalidDataException>(async () => await CreateDownloader(client).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)), "invalid.bin", destination, null));

            Assert.Multiple(() =>
            {
                Assert.That(destination.Length, Is.Zero);
                Assert.That(Directory.EnumerateDirectories(Path.Combine(_root, "cache")), Is.Empty);
            });
        }

        [Test]
        public async Task DownloadFileAsync_VersionChangesBeforeNextChunkDoesNotMixVersionsAfterRestart()
        {
            Guid fileId = Guid.NewGuid();
            byte[] original = Encoding.UTF8.GetBytes("abcdefghijkl");
            byte[] changed = Encoding.UTF8.GetBytes("ABCDEFGHIJKL");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = original;
            client.FilesClient.OnChunkDownloadAsync = async (number, destination, _, token) =>
            {
                if (number > 0)
                {
                    client.FilesClient.Downloads[fileId] = changed;
                    throw new CottonApiException(HttpStatusCode.PreconditionFailed, null, "File version changed.");
                }
                await destination.WriteAsync(original.AsMemory(0, 4), token);
                return 3;
            };
            SdkRemoteFileSynchronizerOptions options = new()
            {
                DownloadCacheDirectory = Path.Combine(_root, "cache"),
                MaxConcurrentChunkDownloads = 1,
            };
            await using MemoryStream interrupted = new();
            Assert.ThrowsAsync<CottonApiException>(async () => await new SdkRemoteFileSynchronizer(client, options)
                .DownloadFileAsync(new RemoteFileDownloadIdentity(fileId, original.Length, ETag(original)),
                    "version.bin", interrupted, null));
            string originalCache = Directory.EnumerateDirectories(options.DownloadCacheDirectory).Single();
            Assert.That(interrupted.Length, Is.Zero);
            Assert.That(client.FilesClient.ChunkDownloads, Has.Count.EqualTo(2));

            client.FilesClient.OnChunkDownloadAsync = null;
            client.FilesClient.DownloadChunkSizeBytes = 4;
            await using MemoryStream resumed = new();
            await new SdkRemoteFileSynchronizer(client, options).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, changed.Length, ETag(changed)), "version.bin", resumed, null);

            Assert.Multiple(() =>
            {
                Assert.That(resumed.ToArray(), Is.EqualTo(changed));
                Assert.That(client.FilesClient.ChunkDownloads.Skip(2).Select(item => item.ExpectedETag),
                    Is.All.EqualTo(ETag(changed)));
                Assert.That(Directory.EnumerateDirectories(options.DownloadCacheDirectory),
                    Is.EqualTo(new[] { originalCache }));
            });
        }

        [Test]
        public async Task DownloadFileAsync_ProgressIncludesBytesBeforeChunkCompletesAndWaitsForAssembly()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefgh");
            FakeCottonCloudClient client = new(chunkSizeBytes: 8);
            client.FilesClient.Downloads[fileId] = content;
            TaskCompletionSource partial = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RecordingProgress<SyncTransferProgress> progress = new();
            client.FilesClient.OnChunkDownloadAsync = async (_, destination, reporter, token) =>
            {
                await destination.WriteAsync(content.AsMemory(0, 3), token);
                reporter?.Report(3);
                partial.TrySetResult();
                await release.Task.WaitAsync(token);
                await destination.WriteAsync(content.AsMemory(3), token);
                reporter?.Report(8);
                return 1;
            };
            await using MemoryStream destination = new();
            Task download = CreateDownloader(client).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)), "progress.bin", destination, progress);
            try
            {
                await partial.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Multiple(() =>
                {
                    Assert.That(progress.Values[^1].TransferredBytes, Is.EqualTo(3));
                    Assert.That(progress.Values.Any(item => item.IsCompleted), Is.False);
                    Assert.That(destination.Length, Is.Zero);
                });
            }
            finally
            {
                release.TrySetResult();
                await download.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Multiple(() =>
            {
                Assert.That(destination.ToArray(), Is.EqualTo(content));
                Assert.That(progress.Values.Count(item => item.IsCompleted), Is.EqualTo(1));
                Assert.That(progress.Values[^1].IsCompleted, Is.True);
            });
        }
    }
}
