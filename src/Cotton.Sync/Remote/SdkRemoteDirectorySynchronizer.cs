// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sdk.Nodes;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Performs remote directory mutations through the Cotton SDK node API.
    /// </summary>
    public class SdkRemoteDirectorySynchronizer : IRemoteDirectorySynchronizer
    {
        private const int DefaultDirectoryPageSize = 100;
        private readonly ICottonNodeClient _nodes;
        private readonly RemoteTreePageReader _pages;

        /// <summary>
        /// Initializes a new instance of the <see cref="SdkRemoteDirectorySynchronizer" /> class.
        /// </summary>
        public SdkRemoteDirectorySynchronizer(ICottonNodeClient nodes, int directoryPageSize = DefaultDirectoryPageSize)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(directoryPageSize);
            _pages = new RemoteTreePageReader(nodes, directoryPageSize);
        }

        /// <inheritdoc />
        public async Task<NodeDto?> FindChildDirectoryAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            string nameKey = RemoteNameKey.Create(name);
            int page = 1;
            int loaded = 0;
            int? expectedTotalCount = null;
            while (true)
            {
                RemoteTreePageReadResult pageResult = await _pages.ReadAsync(
                    parentNodeId,
                    page,
                    loaded,
                    expectedTotalCount,
                    cancellationToken).ConfigureAwait(false);
                NodeContentDto content = pageResult.Children;
                NodeDto? match = content.Nodes.FirstOrDefault(node =>
                    string.Equals(RemoteNameKey.Create(node.Name), nameKey, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }

                int count = content.Nodes.Count + content.Files.Count;
                loaded += count;
                if (loaded == pageResult.TotalCount)
                {
                    return null;
                }

                expectedTotalCount = pageResult.TotalCount;
                page++;
            }
        }

        /// <inheritdoc />
        public Task<NodeDto> CreateDirectoryAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _nodes.CreateAsync(parentNodeId, name.Trim(), cancellationToken);
        }

        /// <inheritdoc />
        public Task DeleteDirectoryAsync(
            Guid nodeId,
            bool skipTrash = false,
            CancellationToken cancellationToken = default)
        {
            return _nodes.DeleteAsync(nodeId, skipTrash, cancellationToken);
        }
    }
}
