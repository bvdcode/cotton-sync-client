// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;
using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Chunks;
using Cotton.Sdk.Files;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Notifications;
using Cotton.Sdk.Realtime;
using Cotton.Sdk.Settings;
using Cotton.Sdk.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        private class FakeNodeClient : ICottonNodeClient
        {
            public Dictionary<Guid, List<NodeDto>> Children { get; } = [];

            public List<NodeDto> CreatedNodes { get; } = [];

            public List<(Guid ParentId, string Name)> ConflictCreates { get; } = [];

            public List<Guid> GetRequests { get; } = [];

            public Task<NodeDto> ResolveAsync(string? path = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> GetAsync(Guid nodeId, CancellationToken cancellationToken = default)
            {
                GetRequests.Add(nodeId);
                NodeDto? node = Children.Values
                    .SelectMany(static children => children)
                    .FirstOrDefault(item => item.Id == nodeId);
                if (node is null)
                {
                    throw new CottonApiException(
                        HttpStatusCode.NotFound,
                        "{\"message\":\"Node not found.\"}",
                        "Cotton API request GET /api/v1/layouts/nodes failed with status 404 (NotFound).");
                }

                return Task.FromResult(node);
            }

            public Task<CottonPagedResult<NodeContentDto>> GetChildrenAsync(
                Guid nodeId,
                int page = 1,
                int pageSize = 100,
                int depth = 0,
                CancellationToken cancellationToken = default)
            {
                List<NodeDto> allChildren = Children.TryGetValue(nodeId, out List<NodeDto>? children) ? children : [];
                List<NodeDto> nodes = allChildren.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                return Task.FromResult(new CottonPagedResult<NodeContentDto>(
                    new NodeContentDto { Nodes = nodes },
                    allChildren.Count));
            }

            public Task<NodeDto> CreateAsync(Guid parentId, string name, CancellationToken cancellationToken = default)
            {
                NodeDto node = Node(Guid.NewGuid(), parentId, name);
                if (!Children.TryGetValue(parentId, out List<NodeDto>? children))
                {
                    children = [];
                    Children[parentId] = children;
                }

                children.Add(node);
                int conflictIndex = ConflictCreates.FindIndex(item =>
                    item.ParentId == parentId
                    && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (conflictIndex >= 0)
                {
                    ConflictCreates.RemoveAt(conflictIndex);
                    throw new CottonApiException(
                        HttpStatusCode.Conflict,
                        "{\"message\":\"A folder with the same name already exists.\"}",
                        "Cotton API request PUT /api/v1/layouts/nodes failed with status 409 (Conflict).");
                }

                CreatedNodes.Add(node);
                return Task.FromResult(node);
            }

            public Task<NodeDto> MoveAsync(Guid nodeId, Guid parentId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> RenameAsync(Guid nodeId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> UpdateMetadataAsync(Guid nodeId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteAsync(Guid nodeId, bool skipTrash = false, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeDto> RestoreAsync(RestoreItemRequestDto? request = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<RestoreOutcomeDto> RestoreAsync(Guid nodeId, RestoreItemRequestDto? request = null, CancellationToken cancellationToken = default)
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
