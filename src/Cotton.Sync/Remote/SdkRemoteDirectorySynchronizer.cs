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
        private readonly RemoteChildDirectoryFinder _directories;

        /// <summary>
        /// Initializes a new instance of the <see cref="SdkRemoteDirectorySynchronizer" /> class.
        /// </summary>
        public SdkRemoteDirectorySynchronizer(ICottonNodeClient nodes, int directoryPageSize = DefaultDirectoryPageSize)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(directoryPageSize);
            _directories = new RemoteChildDirectoryFinder(nodes, directoryPageSize);
        }

        /// <inheritdoc />
        public Task<NodeDto?> FindChildDirectoryAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _directories.FindAsync(parentNodeId, name, cancellationToken);
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
        public Task<NodeDto> RenameDirectoryAsync(
            Guid nodeId,
            string name,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _nodes.RenameAsync(nodeId, name.Trim(), cancellationToken);
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
