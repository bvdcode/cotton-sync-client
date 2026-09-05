// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Supervision
{
    public partial class SyncSupervisorTests
    {
        private class FaultIsolationRemote : IRemoteTreeCrawler, IRemoteFileSynchronizer
        {
            public const int DiskFullHResult = unchecked((int)0x80070070);
            public const string RelativePath = "document.txt";
            private readonly Dictionary<Guid, (NodeFileManifestDto File, byte[] Content)> _files = [];

            public Guid? DiskFullRootNodeId { get; set; }

            public int PartialWriteCount { get; private set; }

            public NodeFileManifestDto GetFile(Guid rootNodeId)
            {
                return _files[rootNodeId].File;
            }

            public void SetContent(Guid rootNodeId, string content)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                Guid fileId = _files.TryGetValue(rootNodeId, out (NodeFileManifestDto File, byte[] Content) existing)
                    ? existing.File.Id
                    : Guid.NewGuid();
                NodeFileManifestDto file = new()
                {
                    Id = fileId,
                    NodeId = rootNodeId,
                    FileManifestId = Guid.NewGuid(),
                    OriginalNodeFileId = fileId,
                    Name = RelativePath,
                    ContentType = "text/plain",
                    ContentHash = hash,
                    ETag = "sha256-" + hash,
                    SizeBytes = bytes.LongLength,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
                    Metadata = [],
                };
                _files[rootNodeId] = (file, bytes);
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new RemoteTreeSnapshot
                {
                    RootNode = new NodeDto { Id = rootNodeId },
                    Files = [new RemoteFileSnapshot { RelativePath = RelativePath, File = GetFile(rootNodeId) }],
                });
            }

            public async Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                (NodeFileManifestDto file, byte[] content) = _files.Values.Single(entry => entry.File.Id == nodeFileId);
                if (file.NodeId == DiskFullRootNodeId)
                {
                    await destination.WriteAsync(content.AsMemory(0, content.Length / 2), cancellationToken);
                    PartialWriteCount++;
                    throw new IOException("The destination disk filled during the download.", DiskFullHResult);
                }

                await destination.WriteAsync(content, cancellationToken);
            }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("An unchanged local file must not be uploaded.");
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("No remote file move is expected.");
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("No remote file deletion is expected.");
            }
        }
    }
}
