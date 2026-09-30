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

        [TestCase("Library/Album")]
        [TestCase("LIBRARY/ALBUM/report.txt")]
        [TestCase("Library/Album/missing.txt")]
        [TestCase("Library/Album-extra")]
        [TestCase("LibraryTwo")]
        [TestCase("missing")]
        public async Task CrawlPathLookupsAsync_MatchesEngineSnapshotCrawlerContract(string requestedPath)
        {
            Guid rootId = Guid.NewGuid();
            Guid libraryId = Guid.NewGuid();
            Guid albumId = Guid.NewGuid();
            Guid otherId = Guid.NewGuid();
            Guid siblingId = Guid.NewGuid();
            FakeNodeClient client = new();
            client.Nodes[rootId] = Node(rootId, null, "root");
            client.Nodes[libraryId] = Node(libraryId, rootId, "Library");
            client.Nodes[albumId] = Node(albumId, libraryId, "Album");
            client.Nodes[otherId] = Node(otherId, libraryId, "Other");
            client.Nodes[siblingId] = Node(siblingId, rootId, "LibraryTwo");
            client.Children[(rootId, 1)] = new FakeNodePage
            {
                TotalCount = 3,
                Nodes = [client.Nodes[libraryId], client.Nodes[siblingId]],
                Files = [File(rootId, "root.txt")],
            };
            client.Children[(libraryId, 1)] = new FakeNodePage
            {
                TotalCount = 3,
                Nodes = [client.Nodes[albumId], client.Nodes[otherId]],
                Files = [File(libraryId, "parent.txt")],
            };
            client.Children[(albumId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Files = [File(albumId, "report.txt")],
            };
            client.Children[(otherId, 1)] = new FakeNodePage
            {
                TotalCount = 1,
                Files = [File(otherId, "sibling.txt")],
            };
            RemoteTreeCrawler crawler = new(client);
            RemoteTreeSnapshot tree = await crawler.CrawlAsync(rootId);
            SyncEngineTests.FakeRemoteTreeCrawler snapshotCrawler = new(tree);

            RemoteTreeLookupSnapshot expected = await crawler.CrawlPathLookupsAsync(rootId, [requestedPath], null);
            RemoteTreeLookupSnapshot actual = await snapshotCrawler.CrawlPathLookupsAsync(rootId, [requestedPath], null);

            Assert.Multiple(() =>
            {
                Assert.That(actual.RootNode.Id, Is.EqualTo(expected.RootNode.Id));
                Assert.That(actual.DirectoriesByPath.Select(entry => (entry.Key, entry.Value.RelativePath, entry.Value.Node.Id)),
                    Is.EquivalentTo(expected.DirectoriesByPath.Select(entry => (entry.Key, entry.Value.RelativePath, entry.Value.Node.Id))));
                Assert.That(actual.FilesByPath.Select(entry => (entry.Key, entry.Value.RelativePath, entry.Value.File.Id)),
                    Is.EquivalentTo(expected.FilesByPath.Select(entry => (entry.Key, entry.Value.RelativePath, entry.Value.File.Id))));
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
