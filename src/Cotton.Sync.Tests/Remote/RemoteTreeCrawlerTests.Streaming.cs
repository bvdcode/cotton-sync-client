// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class RemoteTreeCrawlerTests
    {
        [Test]
        public async Task CrawlStreamingAsync_WalksIndependentBranchesConcurrently()
        {
            Guid rootId = Guid.NewGuid();
            Guid docsId = Guid.NewGuid();
            Guid photosId = Guid.NewGuid();
            Guid videosId = Guid.NewGuid();
            FakeNodeClient client = new FakeNodeClient
            {
                GetChildrenDelay = TimeSpan.FromMilliseconds(50),
            };
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Nodes[docsId] = Node(docsId, rootId, "Docs");
            client.Nodes[photosId] = Node(photosId, rootId, "Photos");
            client.Nodes[videosId] = Node(videosId, rootId, "Videos");
            client.Children[(rootId, 1)] = new FakeNodePage
            {
                TotalCount = 3,
                Nodes = [client.Nodes[docsId], client.Nodes[photosId], client.Nodes[videosId]],
            };
            client.Children[(docsId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Files = [File(docsId, "report.txt")],
            };
            client.Children[(photosId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Files = [File(photosId, "photo.jpg")],
            };
            client.Children[(videosId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Files = [File(videosId, "clip.mp4")],
            };
            RemoteTreeCrawler crawler = new RemoteTreeCrawler(client, pageSize: 1, streamingConcurrency: 3);
            RecordingStreamSink sink = new RecordingStreamSink();
            RecordingProgress<RemoteTreeScanProgress> progress = new RecordingProgress<RemoteTreeScanProgress>();

            await crawler.CrawlStreamingAsync(rootId, sink, progress);

            Assert.Multiple(() =>
            {
                Assert.That(sink.Directories.Select(directory => directory.RelativePath), Is.EquivalentTo(new[] { "Docs", "Photos", "Videos" }));
                Assert.That(sink.Files.Select(file => file.RelativePath), Is.EquivalentTo(new[] { "Docs/report.txt", "Photos/photo.jpg", "Videos/clip.mp4" }));
                Assert.That(client.MaxConcurrentGetChildrenCalls, Is.GreaterThan(1));
                Assert.That(progress.Values[^1].PagesScanned, Is.EqualTo(4));
                Assert.That(progress.Values[^1].EntriesExpected, Is.EqualTo(6));
                Assert.That(progress.Values[^1].PageReadLatencyTotal, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(progress.Values[^1].PageReadLatencyMax, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(progress.Values[^1].LastPageReadLatency, Is.GreaterThan(TimeSpan.Zero));
            });
        }
    }
}
