// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk.Nodes;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public class SdkRemoteDirectorySynchronizerTests
    {
        [Test]
        public async Task CreateDirectoryAsync_And_DeleteDirectoryAsync_DelegateToSdkNodeClient()
        {
            Guid parentId = Guid.NewGuid();
            var client = new FakeNodeClient();
            var synchronizer = new SdkRemoteDirectorySynchronizer(client);

            NodeDto created = await synchronizer.CreateDirectoryAsync(parentId, " Reports ");
            await synchronizer.DeleteDirectoryAsync(created.Id, skipTrash: true);

            Assert.Multiple(() =>
            {
                Assert.That(client.CreatedDirectories, Is.EqualTo(new[] { (parentId, "Reports") }));
                Assert.That(created.ParentId, Is.EqualTo(parentId));
                Assert.That(created.Name, Is.EqualTo("Reports"));
                Assert.That(client.DeletedDirectories, Is.EqualTo(new[] { (created.Id, true) }));
            });
        }

        [Test]
        public async Task FindChildDirectoryAsync_ReturnsCaseInsensitiveChildDirectory()
        {
            Guid parentId = Guid.NewGuid();
            var child = new NodeDto
            {
                Id = Guid.NewGuid(),
                ParentId = parentId,
                Name = "Reports",
            };
            var client = new FakeNodeClient();
            client.Children[parentId] = [child];
            var synchronizer = new SdkRemoteDirectorySynchronizer(client);

            NodeDto? found = await synchronizer.FindChildDirectoryAsync(parentId, " reports ");

            Assert.Multiple(() =>
            {
                Assert.That(found, Is.SameAs(child));
                Assert.That(client.GetChildrenCalls, Is.EqualTo(new[] { (parentId, 1, 100, 0) }));
            });
        }

        [Test]
        public async Task FindChildDirectoryAsync_ReturnsNullWhenChildDirectoryIsMissing()
        {
            Guid parentId = Guid.NewGuid();
            var client = new FakeNodeClient();
            var synchronizer = new SdkRemoteDirectorySynchronizer(client);

            NodeDto? found = await synchronizer.FindChildDirectoryAsync(parentId, "Missing");

            Assert.That(found, Is.Null);
        }

        private class FakeNodeClient : ICottonNodeClient
        {
            public List<(Guid ParentId, string Name)> CreatedDirectories { get; } = [];

            public List<(Guid NodeId, bool SkipTrash)> DeletedDirectories { get; } = [];

            public Dictionary<Guid, List<NodeDto>> Children { get; } = [];

            public List<(Guid NodeId, int Page, int PageSize, int Depth)> GetChildrenCalls { get; } = [];

            public Task<NodeDto> ResolveAsync(string? path = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> GetAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeContentDto> GetChildrenAsync(
                Guid nodeId,
                int page = 1,
                int pageSize = 100,
                int depth = 0,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GetChildrenCalls.Add((nodeId, page, pageSize, depth));
                Children.TryGetValue(nodeId, out List<NodeDto>? children);
                children ??= [];
                return Task.FromResult(new NodeContentDto
                {
                    Nodes = children,
                    TotalCount = children.Count,
                });
            }

            public Task<NodeDto> CreateAsync(Guid parentId, string name, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreatedDirectories.Add((parentId, name));
                return Task.FromResult(new NodeDto
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentId,
                    Name = name,
                });
            }

            public Task<NodeDto> MoveAsync(Guid nodeId, Guid parentId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> RenameAsync(Guid nodeId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> UpdateMetadataAsync(
                Guid nodeId,
                IReadOnlyDictionary<string, string> metadata,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(Guid nodeId, bool skipTrash = false, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeletedDirectories.Add((nodeId, skipTrash));
                return Task.CompletedTask;
            }

            public Task<RestoreOutcomeDto> RestoreAsync(
                Guid nodeId,
                RestoreItemRequestDto? request = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<List<NodeDto>> GetAncestorsAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
