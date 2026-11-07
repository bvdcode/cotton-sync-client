// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class DirectoryRenameRemoteFixture(NodeDto root) :
        IRemoteTreeCrawler, IRemotePathLookupCrawler, IRemoteDirectorySynchronizer, IRemoteFileSynchronizer
    {
        private readonly Dictionary<string, NodeDto> _directories = new(StringComparer.OrdinalIgnoreCase);

        public int Creates { get; private set; }
        public int Deletes { get; private set; }
        public int Renames { get; private set; }

        public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RemoteTreeSnapshot
            {
                RootNode = root,
                Directories = _directories.Select(entry => new RemoteDirectorySnapshot
                {
                    RelativePath = entry.Key,
                    Node = entry.Value,
                }).ToList(),
            });
        }

        public Task<NodeDto?> FindChildDirectoryAsync(Guid parentNodeId, string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_directories.Values.SingleOrDefault(node =>
                node.ParentId == parentNodeId && node.Name == name));
        }

        public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(Guid rootNodeId,
            IReadOnlyCollection<string> relativePaths, IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoteTreeLookupSnapshot snapshot = new() { RootNode = root };
            foreach ((string path, NodeDto node) in _directories)
            {
                snapshot.DirectoriesByPath.Add(SyncPath.ToKey(path),
                    new RemoteDirectorySnapshot { RelativePath = path, Node = node });
            }

            return Task.FromResult(snapshot);
        }

        public Task<NodeDto> CreateDirectoryAsync(Guid parentNodeId, string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string parentPath = parentNodeId == root.Id
                ? string.Empty
                : _directories.Single(entry => entry.Value.Id == parentNodeId).Key + "/";
            NodeDto node = new()
            {
                Id = Guid.NewGuid(), ParentId = parentNodeId, Name = name,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _directories.Add(parentPath + name, node);
            Creates++;
            return Task.FromResult(node);
        }

        public Task<NodeDto> RenameDirectoryAsync(Guid nodeId, string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyValuePair<string, NodeDto> source = _directories.Single(entry => entry.Value.Id == nodeId);
            string parentPath = source.Key[..(source.Key.Length - source.Value.Name.Length)];
            _directories.Remove(source.Key);
            NodeDto renamed = new()
            {
                Id = source.Value.Id, ParentId = source.Value.ParentId, Name = name,
                CreatedAt = source.Value.CreatedAt, UpdatedAt = DateTime.UtcNow,
            };
            _directories.Add(parentPath + name, renamed);
            Renames++;
            return Task.FromResult(renamed);
        }

        public Task DeleteDirectoryAsync(Guid nodeId, bool skipTrash = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _directories.Remove(_directories.Single(entry => entry.Value.Id == nodeId).Key);
            Deletes++;
            return Task.CompletedTask;
        }

        public Task<NodeFileManifestDto> UploadFileAsync(Guid rootNodeId, string relativePath,
            LocalFileSnapshot localFile, NodeFileManifestDto? existingRemoteFile = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DownloadFileAsync(Guid nodeFileId, Stream destination,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeFileManifestDto> MoveFileAsync(Guid rootNodeId, string relativePath,
            NodeFileManifestDto existingRemoteFile, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteFileAsync(Guid nodeFileId, bool skipTrash = false, string? expectedETag = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
