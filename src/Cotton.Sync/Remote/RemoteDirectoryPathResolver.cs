// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using System.Net;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sdk.Nodes;
using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    internal class RemoteDirectoryPathResolver
    {
        private readonly ConcurrentDictionary<string, Guid> _directoryCache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ICottonNodeClient _nodes;
        private readonly RemoteChildDirectoryFinder _directories;

        public RemoteDirectoryPathResolver(ICottonNodeClient nodes, int pageSize)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
            _directories = new RemoteChildDirectoryFinder(nodes, pageSize);
        }

        public async Task<Guid> EnsureParentAsync(
            Guid rootNodeId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            string[] segments = relativePath.Split('/');
            if (segments.Length == 1)
            {
                return rootNodeId;
            }

            Guid currentNodeId = rootNodeId;
            string currentPath = string.Empty;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                string segment = segments[index];
                currentPath = string.IsNullOrEmpty(currentPath) ? segment : currentPath + "/" + segment;
                string cacheKey = rootNodeId.ToString("D") + ":" + SyncPath.ToKey(currentPath);
                Guid? cachedNodeId = await TryGetCachedAsync(
                        cacheKey,
                        currentNodeId,
                        segment,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cachedNodeId.HasValue)
                {
                    currentNodeId = cachedNodeId.Value;
                    continue;
                }

                NodeDto? existing = await FindChildAsync(currentNodeId, segment, cancellationToken)
                    .ConfigureAwait(false);
                NodeDto node = existing
                    ?? await CreateOrReuseAsync(currentNodeId, segment, cancellationToken).ConfigureAwait(false);
                currentNodeId = node.Id;
                _directoryCache[cacheKey] = currentNodeId;
            }

            return currentNodeId;
        }

        private async Task<NodeDto> CreateOrReuseAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _nodes.CreateAsync(parentNodeId, name, cancellationToken).ConfigureAwait(false);
            }
            catch (CottonApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                NodeDto? existing = await FindChildAsync(parentNodeId, name, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }

                return existing;
            }
        }

        private async Task<Guid?> TryGetCachedAsync(
            string cacheKey,
            Guid expectedParentNodeId,
            string expectedName,
            CancellationToken cancellationToken)
        {
            if (!_directoryCache.TryGetValue(cacheKey, out Guid cachedNodeId))
            {
                return null;
            }

            NodeDto cachedNode;
            try
            {
                cachedNode = await _nodes.GetAsync(cachedNodeId, cancellationToken).ConfigureAwait(false);
            }
            catch (CottonApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                _directoryCache.TryRemove(cacheKey, out _);
                return null;
            }

            if (cachedNode.ParentId != expectedParentNodeId
                || !string.Equals(
                    RemoteNameKey.Create(cachedNode.Name),
                    RemoteNameKey.Create(expectedName),
                    StringComparison.Ordinal))
            {
                _directoryCache.TryRemove(cacheKey, out _);
                return null;
            }

            return cachedNode.Id;
        }

        private Task<NodeDto?> FindChildAsync(Guid parentNodeId, string name, CancellationToken cancellationToken)
        {
            return _directories.FindAsync(parentNodeId, name, cancellationToken);
        }
    }
}
