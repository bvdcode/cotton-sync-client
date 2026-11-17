// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sdk.Nodes;

namespace Cotton.Sync.Remote
{
    internal class RemoteChildDirectoryFinder(ICottonNodeClient nodes, int pageSize)
    {
        private readonly RemoteTreePageReader _pages = new(nodes, pageSize);

        public async Task<NodeDto?> FindAsync(Guid parentNodeId, string name, CancellationToken cancellationToken)
        {
            string nameKey = RemoteNameKey.Create(name);
            int page = 1;
            int loaded = 0;
            int? expectedTotalCount = null;
            while (true)
            {
                RemoteTreePageReadResult pageResult = await _pages.ReadAsync(
                    parentNodeId, page, loaded, expectedTotalCount, cancellationToken).ConfigureAwait(false);
                NodeContentDto content = pageResult.Children;
                NodeDto? match = content.Nodes.FirstOrDefault(node =>
                    string.Equals(RemoteNameKey.Create(node.Name), nameKey, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
                loaded += content.Nodes.Count + content.Files.Count;
                if (loaded == pageResult.TotalCount)
                {
                    return null;
                }
                expectedTotalCount = pageResult.TotalCount;
                page++;
            }
        }
    }
}
