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
        private readonly int _pageSize;

        public RemoteDirectoryPathResolver(ICottonNodeClient nodes, int pageSize)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
            _pageSize = pageSize;
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

        private async Task<NodeDto?> FindChildAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken)
        {
            string nameKey = RemoteNameKey.Create(name);
            int page = 1;
            int loaded = 0;
            while (true)
            {
                CottonPagedResult<NodeContentDto> pageResult = await _nodes.GetChildrenAsync(
                    parentNodeId,
                    page,
                    _pageSize,
                    depth: 0,
                    cancellationToken).ConfigureAwait(false);
                NodeContentDto content = pageResult.Payload;
                NodeDto? match = content.Nodes.FirstOrDefault(node =>
                    string.Equals(RemoteNameKey.Create(node.Name), nameKey, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }

                int count = content.Nodes.Count + content.Files.Count;
                loaded += count;
                if (count == 0 || loaded >= pageResult.TotalCount)
                {
                    return null;
                }

                page++;
            }
        }
    }
}
