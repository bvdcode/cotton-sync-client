// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class RemoteTreeCrawlerTests
    {
        [Test]
        public async Task CrawlPathLookupsAsync_CrawlsEveryPagedDirectoryDescendant()
        {
            Guid rootId = Guid.NewGuid();
            Guid libraryId = Guid.NewGuid();
            FakeNodeClient client = new FakeNodeClient();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Nodes[libraryId] = Node(libraryId, rootId, "Library");
            client.Children[(rootId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Nodes = [client.Nodes[libraryId]],
            };
            client.Children[(libraryId, 1)] = CreateFilePage(libraryId, 0, 50, totalCount: 101);
            client.Children[(libraryId, 2)] = CreateFilePage(libraryId, 50, 50, totalCount: 101);
            client.Children[(libraryId, 3)] = CreateFilePage(libraryId, 100, 1, totalCount: 101);
            RemoteTreeCrawler crawler = new RemoteTreeCrawler(client, pageSize: 50);

            RemoteTreeLookupSnapshot snapshot = await crawler.CrawlPathLookupsAsync(rootId, ["Library"], null);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.DirectoriesByPath.Keys, Is.EqualTo(new[] { "LIBRARY" }));
                Assert.That(snapshot.FilesByPath, Has.Count.EqualTo(101));
                Assert.That(snapshot.FilesByPath.ContainsKey("LIBRARY/FILE-000.TXT"), Is.True);
                Assert.That(snapshot.FilesByPath.ContainsKey("LIBRARY/FILE-100.TXT"), Is.True);
                Assert.That(
                    client.GetChildrenCalls,
                    Is.EqualTo(new[]
                    {
                        (rootId, 1),
                        (libraryId, 1),
                        (libraryId, 2),
                        (libraryId, 3),
                    }));
            });
        }

        private static FakeNodePage CreateFilePage(Guid nodeId, int firstIndex, int count, int totalCount)
        {
            List<NodeFileManifestDto> files = Enumerable.Range(firstIndex, count)
                .Select(index => File(nodeId, "file-" + index.ToString("D3") + ".txt"))
                .ToList();
            return new FakeNodePage
            {
                TotalCount = totalCount,
                Files = files,
            };
        }
    }
}
