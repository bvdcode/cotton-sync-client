// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net.Sockets;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public async Task DownloadFileAsync_RetriesInterruptedSocketButNotLocalIoFailure(bool socketFailure)
        {
            byte[] content = [1, 2, 3, 4, 5, 6, 7, 8];
            Guid fileId = Guid.NewGuid();
            FakeCottonCloudClient client = new(chunkSizeBytes: content.Length);
            client.FilesClient.Downloads[fileId] = content;
            int attempts = 0;
            client.FilesClient.OnChunkDownloadAsync = async (_, destination, _, token) =>
            {
                if (++attempts == 1)
                {
                    await destination.WriteAsync(content.AsMemory(0, 3), token);
                    if (socketFailure)
                    {
                        throw new IOException("Transport read failed.", new SocketException((int)SocketError.ConnectionReset));
                    }
                    throw new IOException("Local disk write failed.");
                }
                await destination.WriteAsync(content, token);
                return 1;
            };
            await using MemoryStream destination = new();
            Task download = CreateDownloader(client).DownloadFileAsync(
                new RemoteFileDownloadIdentity(fileId, content.Length, ETag(content)), "file.bin", destination, null);
            if (socketFailure)
            {
                await download;
                Assert.That(attempts, Is.EqualTo(2));
                Assert.That(destination.ToArray(), Is.EqualTo(content));
            }
            else
            {
                Assert.ThrowsAsync<IOException>(() => download);
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(destination.Length, Is.Zero);
            }
        }
    }
}
