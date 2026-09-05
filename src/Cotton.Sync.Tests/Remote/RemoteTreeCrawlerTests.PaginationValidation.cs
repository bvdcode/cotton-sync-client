// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class RemoteTreeCrawlerTests
    {
        [TestCase(500, false)]
        [TestCase(501, false)]
        [TestCase(502, false)]
        [TestCase(500, true)]
        [TestCase(501, true)]
        [TestCase(502, true)]
        public void CrawlAsync_RejectsChangedOrIncompleteListing(int nextTotalCount, bool useLookups)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, 500, nextTotalCount);
            RemoteTreeCrawler crawler = new(client);

            IOException? exception = Assert.ThrowsAsync<IOException>(async () =>
            {
                if (useLookups)
                {
                    await crawler.CrawlLookupsAsync(rootId, null);
                }
                else
                {
                    await crawler.CrawlAsync(rootId);
                }
            });

            AssertFailedListing(exception, client, rootId, requestedPages: 2);
        }

        [TestCase(500)]
        [TestCase(501)]
        [TestCase(502)]
        public void CrawlPathLookupsAsync_RejectsChangedOrIncompleteListing(int nextTotalCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, 500, nextTotalCount);
            RemoteTreeCrawler crawler = new(client);

            IOException? exception = Assert.ThrowsAsync<IOException>(
                () => crawler.CrawlPathLookupsAsync(rootId, ["file-500.txt"], null));

            AssertFailedListing(exception, client, rootId, requestedPages: 2);
        }

        [TestCase(500)]
        [TestCase(501)]
        [TestCase(502)]
        public void CrawlStreamingAsync_RejectsChangedOrIncompleteListing(int nextTotalCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, 500, nextTotalCount);
            RemoteTreeCrawler crawler = new(client);
            RecordingStreamSink sink = new();
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

            IOException? exception = Assert.ThrowsAsync<IOException>(
                () => crawler.CrawlStreamingAsync(rootId, sink, null, cancellation.Token));

            AssertFailedListing(exception, client, rootId, requestedPages: 2);
            Assert.That(sink.Files, Has.Count.EqualTo(500));
        }

        [TestCase(0)]
        [TestCase(499)]
        public void CrawlAsync_RejectsIncompleteFirstPage(int firstPageCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, firstPageCount, 501);
            RemoteTreeCrawler crawler = new(client);

            IOException? exception = Assert.ThrowsAsync<IOException>(() => crawler.CrawlAsync(rootId));

            AssertFailedListing(exception, client, rootId, requestedPages: 1);
        }

        [TestCase(0)]
        [TestCase(499)]
        public void CrawlPathLookupsAsync_RejectsIncompleteFirstPage(int firstPageCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, firstPageCount, 501);
            RemoteTreeCrawler crawler = new(client);

            IOException? exception = Assert.ThrowsAsync<IOException>(
                () => crawler.CrawlPathLookupsAsync(rootId, ["file-500.txt"], null));

            AssertFailedListing(exception, client, rootId, requestedPages: 1);
        }

        [TestCase(0)]
        [TestCase(499)]
        public void CrawlStreamingAsync_RejectsIncompleteFirstPage(int firstPageCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, firstPageCount, 501);
            RemoteTreeCrawler crawler = new(client);
            RecordingStreamSink sink = new();
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

            IOException? exception = Assert.ThrowsAsync<IOException>(
                () => crawler.CrawlStreamingAsync(rootId, sink, null, cancellation.Token));

            AssertFailedListing(exception, client, rootId, requestedPages: 1);
            Assert.That(sink.Files, Is.Empty);
        }

        [TestCase(500, 500)]
        [TestCase(500, 501)]
        [TestCase(499, 501)]
        public void EnsureParentAsync_RejectsChangedOrIncompleteListing(int firstPageCount, int nextTotalCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, firstPageCount, nextTotalCount);
            RemoteDirectoryPathResolver resolver = new(client, pageSize: 500);

            IOException? exception = Assert.ThrowsAsync<IOException>(
                () => resolver.EnsureParentAsync(rootId, "Missing/file.txt", CancellationToken.None));

            AssertFailedListing(exception, client, rootId, requestedPages: firstPageCount == 500 ? 2 : 1);
        }

        [TestCase(500, 500)]
        [TestCase(500, 501)]
        [TestCase(499, 501)]
        public void FindChildDirectoryAsync_RejectsChangedOrIncompleteListing(int firstPageCount, int nextTotalCount)
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = CreateIncompleteListingClient(rootId, firstPageCount, nextTotalCount);
            SdkRemoteDirectorySynchronizer synchronizer = new(client, directoryPageSize: 500);

            IOException? exception = Assert.ThrowsAsync<IOException>(
                () => synchronizer.FindChildDirectoryAsync(rootId, "Missing"));

            AssertFailedListing(exception, client, rootId, requestedPages: firstPageCount == 500 ? 2 : 1);
        }

        private static FakeNodeClient CreateIncompleteListingClient(Guid rootId, int firstPageCount, int nextTotalCount)
        {
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Children[(rootId, 1)] = CreateFilePage(rootId, 0, firstPageCount, totalCount: 501);
            client.Children[(rootId, 2)] = new FakeNodePage { TotalCount = nextTotalCount };
            return client;
        }

        private static void AssertFailedListing(
            IOException? exception,
            FakeNodeClient client,
            Guid rootId,
            int requestedPages)
        {
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("listing").And.Contain(rootId.ToString("D")));
                Assert.That(
                    client.GetChildrenCalls,
                    Is.EqualTo(Enumerable.Range(1, requestedPages).Select(page => (rootId, page))));
            });
        }
    }
}
