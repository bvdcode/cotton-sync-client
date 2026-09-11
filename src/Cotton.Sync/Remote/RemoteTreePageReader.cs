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

        public Task<RemoteTreePageReadResult> ReadAsync(
            RemoteCrawlFrame frame,
            CancellationToken cancellationToken)
        {
            return ReadAsync(frame.Node.Id, frame.Page, frame.Loaded, frame.ExpectedTotalCount, cancellationToken);
        }

        public async Task<RemoteTreePageReadResult> ReadAsync(
            Guid nodeId,
            int page,
            int loaded,
            int? expectedTotalCount,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CottonPagedResult<NodeContentDto> result = await _nodes.GetChildrenAsync(
                nodeId,
                page,
                _pageSize,
                depth: 0,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (expectedTotalCount.HasValue && result.TotalCount != expectedTotalCount.Value)
            {
                throw new IOException(
                    $"Remote directory listing for node {nodeId:D} changed on page {page}: "
                    + $"expected total {expectedTotalCount.Value}, received {result.TotalCount}.");
            }

            int count = result.Payload.Nodes.Count + result.Payload.Files.Count;
            int expectedPageCount = Math.Min(_pageSize, Math.Max(0, result.TotalCount - loaded));
            if (result.TotalCount < loaded || count != expectedPageCount)
            {
                throw new IOException(
                    $"Remote directory listing for node {nodeId:D} is incomplete or inconsistent on page {page}: "
                    + $"expected {expectedPageCount} entries, received {count}.");
            }

            return new RemoteTreePageReadResult(result.Payload, result.TotalCount, stopwatch.Elapsed);
        }
    }
}
