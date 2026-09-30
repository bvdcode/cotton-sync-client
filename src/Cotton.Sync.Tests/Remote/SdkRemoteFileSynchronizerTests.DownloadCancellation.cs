// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        [Test]
        public async Task DownloadFileAsync_CancelThenRestartPreservesCompletedChunksAndLatestDownload()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijkl");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            TaskCompletionSource partial = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.FilesClient.OnChunkDownloadAsync = async (number, destination, progress, token) =>
            {
                int length = number == 1 ? 2 : 4;
                await destination.WriteAsync(content.AsMemory(number * 4, length), token);
                progress?.Report(length);
                if (number == 1)
                {
                    partial.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return 3;
            };
            SdkRemoteFileSynchronizerOptions options = new()
            {
                DownloadCacheDirectory = Path.Combine(_root, "cache"),
                MaxConcurrentChunkDownloads = 1,
            };
            using CancellationTokenSource cancellation = new();
            await using MemoryStream interrupted = new();
            Task first = new SdkRemoteFileSynchronizer(client, options).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)),
                "cancel.bin", interrupted, null, cancellation.Token);
            await partial.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            Assert.CatchAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));

            client.FilesClient.OnChunkDownloadAsync = null;
            client.FilesClient.DownloadChunkSizeBytes = 4;
            await using MemoryStream resumed = new();
            await new SdkRemoteFileSynchronizer(client, options).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)), "cancel.bin", resumed, null);

            Assert.Multiple(() =>
            {
                Assert.That(interrupted.Length, Is.Zero);
                Assert.That(resumed.ToArray(), Is.EqualTo(content));
                Assert.That(client.FilesClient.ChunkDownloads.Select(item => item.ChunkNumber),
                    Is.EqualTo(new[] { 0, 1, 0, 1, 2 }));
                Assert.That(Directory.EnumerateDirectories(options.DownloadCacheDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task DownloadFileAsync_CancelWaitingDownloadDoesNotCancelActiveOrNextDownload()
        {
            Guid fileId = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("abcdefghijkl");
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            client.FilesClient.Downloads[fileId] = content;
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.FilesClient.OnChunkDownloadAsync = async (number, destination, progress, token) =>
            {
                if (number == 0)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                await destination.WriteAsync(content.AsMemory(number * 4, 4), token);
                progress?.Report(4);
                return 3;
            };
            SdkRemoteFileSynchronizer downloader = CreateDownloader(client);
            RemoteFileDownloadIdentity identity = new(fileId, content.Length, ETag(content));
            await using MemoryStream active = new();
            await using MemoryStream waiting = new();
            using CancellationTokenSource cancellation = new();
            Task first = downloader.DownloadFileAsync(identity, "shared.bin", active, null);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task second = downloader.DownloadFileAsync(identity, "shared.bin", waiting, null, cancellation.Token);
                await cancellation.CancelAsync();
                Assert.CatchAsync<OperationCanceledException>(async () => await second.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(client.FilesClient.ChunkDownloads, Has.Count.EqualTo(1));
            }
            finally
            {
                release.TrySetResult();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await using MemoryStream next = new();
            await downloader.DownloadFileAsync(identity, "shared.bin", next, null);

            Assert.Multiple(() =>
            {
                Assert.That(active.ToArray(), Is.EqualTo(content));
                Assert.That(waiting.Length, Is.Zero);
                Assert.That(next.ToArray(), Is.EqualTo(content));
                Assert.That(Directory.EnumerateDirectories(Path.Combine(_root, "cache")), Is.Empty);
            });
        }

        [Test]
        public async Task DownloadFileAsync_CanceledEmptyFileDoesNotReportCompleted()
        {
            FakeCottonCloudClient client = new(chunkSizeBytes: 4);
            RecordingProgress<SyncTransferProgress> progress = new();
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();
            await using MemoryStream destination = new();

            Assert.CatchAsync<OperationCanceledException>(async () => await CreateDownloader(client).DownloadFileAsync(
                new RemoteFileDownloadIdentity(Guid.NewGuid(), 0, ETag([])),
                "empty.bin", destination, progress, cancellation.Token));

            Assert.That(progress.Values.Any(item => item.IsCompleted), Is.False);
            Assert.That(client.FilesClient.ChunkDownloads, Is.Empty);
        }
    }
}
