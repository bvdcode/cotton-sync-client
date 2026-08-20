// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sdk.Nodes;

namespace Cotton.Sync.Remote
{
    internal class RemoteTreePageReader
    {
        private readonly ICottonNodeClient _nodes;
        private readonly int _pageSize;

        public RemoteTreePageReader(ICottonNodeClient nodes, int pageSize)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
            _pageSize = pageSize;
        }

        public async Task<RemoteTreePageReadResult> ReadAsync(
            RemoteCrawlFrame frame,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CottonPagedResult<NodeContentDto> result = await _nodes.GetChildrenAsync(
                frame.Node.Id,
                frame.Page,
                _pageSize,
                depth: 0,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new RemoteTreePageReadResult(result.Payload, result.TotalCount, stopwatch.Elapsed);
        }

        public async Task<NodeContentDto> FindContainingAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken)
        {
            int page = 1;
            int loaded = 0;
            while (true)
            {
                CottonPagedResult<NodeContentDto> result = await _nodes.GetChildrenAsync(
                    parentNodeId,
                    page,
                    _pageSize,
                    depth: 0,
                    cancellationToken).ConfigureAwait(false);
                NodeContentDto children = result.Payload;
                if (children.Nodes.Any(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                    || children.Files.Any(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return children;
                }

                int count = children.Nodes.Count + children.Files.Count;
                loaded += count;
                if (count == 0 || loaded >= result.TotalCount)
                {
                    return children;
                }

                page++;
            }
        }
    }
}
