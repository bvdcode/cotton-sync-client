// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sdk;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class RemoteTreeCrawlerTests
    {
        [TestCase("snapshot")]
        [TestCase("lookups")]
        [TestCase("paths")]
        [TestCase("stream")]
        public async Task CrawlAsync_DisappearingChildAbortsListingAndNextScanUsesCurrentTree(string mode)
        {
            Guid rootId = Guid.NewGuid();
            Guid oldId = Guid.NewGuid();
            Guid replacementId = Guid.NewGuid();
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Nodes[oldId] = Node(oldId, rootId, "old");
            client.Nodes[replacementId] = Node(replacementId, rootId, "renamed");
            client.Children[(rootId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Nodes = [client.Nodes[oldId]],
            };
            CottonApiException original = new(HttpStatusCode.NotFound, "Folder not found.", "Folder disappeared.");
            client.ChildrenFailures[(oldId, 1)] = original;
            RemoteTreeCrawler crawler = new(client);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

            RemoteDirectoryListingUnavailableException? failure =
                Assert.ThrowsAsync<RemoteDirectoryListingUnavailableException>(
                    () => ReadDisappearingDirectoryAsync(crawler, rootId, mode, timeout.Token));

            Assert.That(failure!.InnerException, Is.SameAs(original));
            client.Children[(rootId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Nodes = [client.Nodes[replacementId]],
            };
            client.Children[(replacementId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Files = [File(replacementId, "content.txt")],
            };

            RemoteTreeSnapshot recovered = await crawler.CrawlAsync(rootId, cancellationToken: timeout.Token);

            Assert.Multiple(() =>
            {
                Assert.That(recovered.Directories.Select(item => item.RelativePath), Is.EqualTo(new[] { "renamed" }));
                Assert.That(recovered.Files.Select(item => item.RelativePath), Is.EqualTo(new[] { "renamed/content.txt" }));
            });
        }

        [TestCase(HttpStatusCode.NotFound)]
        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void CrawlAsync_UnavailableRootKeepsOriginalFailure(HttpStatusCode status)
        {
            CottonApiException original = new(status, "Root unavailable.", "Cannot read sync root.");
            RemoteTreeCrawler crawler = new(new FakeNodeClient { GetFailure = original });

            CottonApiException? failure = Assert.ThrowsAsync<CottonApiException>(() => crawler.CrawlAsync(Guid.NewGuid()));

            Assert.That(failure, Is.SameAs(original));
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void CrawlAsync_ListingPermissionFailureIsNotTreatedAsConcurrentDeletion(HttpStatusCode status)
        {
            Guid rootId = Guid.NewGuid();
            CottonApiException original = new(status, "Access denied.", "Cannot read listing.");
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.ChildrenFailures[(rootId, 1)] = original;
            RemoteTreeCrawler crawler = new(client);

            CottonApiException? failure = Assert.ThrowsAsync<CottonApiException>(() => crawler.CrawlAsync(rootId));

            Assert.That(failure, Is.SameAs(original));
        }

        private static Task ReadDisappearingDirectoryAsync(
            RemoteTreeCrawler crawler, Guid rootId, string mode, CancellationToken cancellationToken)
        {
            switch (mode)
            {
                case "snapshot":
                    return crawler.CrawlAsync(rootId, cancellationToken: cancellationToken);
                case "lookups":
                    return crawler.CrawlLookupsAsync(rootId, null, cancellationToken);
                case "paths":
                    return crawler.CrawlPathLookupsAsync(rootId, ["old/content.txt"], null, cancellationToken);
                case "stream":
                    return crawler.CrawlStreamingAsync(rootId, new RecordingStreamSink(), null, cancellationToken);
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown crawl mode.");
            }
        }
    }
}
