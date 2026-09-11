// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class RemoteTreeCrawlerTests
    {
        [Test]
        public async Task CrawlPathLookupsAsync_ReadsSharedAncestorAndFilePagesOnce()
        {
            Guid rootId = Guid.NewGuid();
            Guid libraryId = Guid.NewGuid();
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Nodes[libraryId] = Node(libraryId, rootId, "Library");
            client.Children[(rootId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Nodes = [client.Nodes[libraryId]],
            };
            const int fileCount = 1000;
            const int pageSize = 50;
            for (int page = 1; page <= fileCount / pageSize; page++)
            {
                client.Children[(libraryId, page)] = CreateFilePage(libraryId, (page - 1) * pageSize, pageSize, fileCount);
            }
            string[] paths = Enumerable.Range(0, fileCount)
                .Select(index => "Library/file-" + index.ToString("D3") + ".txt")
                .ToArray();
            RemoteTreeCrawler crawler = new(client, pageSize);
            RecordingProgress<RemoteTreeScanProgress> progress = new();

            RemoteTreeLookupSnapshot snapshot = await crawler.CrawlPathLookupsAsync(rootId, paths, progress);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.FilesByPath, Has.Count.EqualTo(fileCount));
                Assert.That(snapshot.DirectoriesByPath, Has.Count.EqualTo(1));
                Assert.That(client.GetChildrenCalls, Has.Count.EqualTo(1 + fileCount / pageSize));
                Assert.That(client.GetChildrenCalls.Distinct().Count(), Is.EqualTo(client.GetChildrenCalls.Count));
                Assert.That(progress.Values[^1].FilesScanned, Is.EqualTo(fileCount));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CrawlPathLookupsAsync_CrawlsOverlappingDirectoryScopeOnce(bool directoryFirst)
        {
            Guid rootId = Guid.NewGuid();
            Guid libraryId = Guid.NewGuid();
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Nodes[libraryId] = Node(libraryId, rootId, "Library");
            client.Children[(rootId, 1)] = new FakeNodePage { TotalCount = 1, Nodes = [client.Nodes[libraryId]] };
            client.Children[(libraryId, 1)] = CreateFilePage(libraryId, 0, 50, 101);
            client.Children[(libraryId, 2)] = CreateFilePage(libraryId, 50, 50, 101);
            client.Children[(libraryId, 3)] = CreateFilePage(libraryId, 100, 1, 101);
            string[] paths = directoryFirst
                ? ["Library", "library/file-099.txt", "Library/file-100.txt"]
                : ["Library/file-099.txt", "library/file-100.txt", "Library"];
            RemoteTreeCrawler crawler = new(client, pageSize: 50);
            RecordingProgress<RemoteTreeScanProgress> progress = new();

            RemoteTreeLookupSnapshot snapshot = await crawler.CrawlPathLookupsAsync(rootId, paths, progress);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.FilesByPath, Has.Count.EqualTo(101));
                Assert.That(client.GetChildrenCalls, Has.Count.EqualTo(4));
                Assert.That(progress.Values.Select(value => value.FilesScanned), Is.Ordered);
                Assert.That(progress.Values[^1].PagesScanned, Is.EqualTo(4));
            });
        }

        [Test]
        public async Task CrawlPathLookupsAsync_ExcludesUnrequestedFilesAndStopsAfterLastMatch()
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Children[(rootId, 1)] = CreateFilePage(rootId, 0, 50, 150);
            client.Children[(rootId, 2)] = CreateFilePage(rootId, 50, 50, 150);
            RemoteTreeCrawler crawler = new(client, pageSize: 50);

            RemoteTreeLookupSnapshot snapshot = await crawler.CrawlPathLookupsAsync(
                rootId, ["file-070.txt", "FILE-002.TXT", "file-070.txt"], null);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.FilesByPath.Keys, Is.EquivalentTo(new[] { "FILE-002.TXT", "FILE-070.TXT" }));
                Assert.That(client.GetChildrenCalls, Is.EqualTo(new[] { (rootId, 1), (rootId, 2) }));
            });
        }

        [Test]
        public async Task CrawlPathLookupsAsync_DoesNotReuseListingAcrossRuns()
        {
            Guid rootId = Guid.NewGuid();
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Children[(rootId, 1)] = CreateFilePage(rootId, 0, 1, 1);
            RemoteTreeCrawler crawler = new(client);
            RemoteTreeLookupSnapshot first = await crawler.CrawlPathLookupsAsync(rootId, ["file-000.txt"], null);
            client.Children[(rootId, 1)] = new FakeNodePage { TotalCount = 0 };

            RemoteTreeLookupSnapshot second = await crawler.CrawlPathLookupsAsync(rootId, ["file-000.txt"], null);

            Assert.Multiple(() =>
            {
                Assert.That(first.FilesByPath, Has.Count.EqualTo(1));
                Assert.That(second.FilesByPath, Is.Empty);
                Assert.That(client.GetChildrenCalls, Has.Count.EqualTo(2));
            });
        }
    }
}
