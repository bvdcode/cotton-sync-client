// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {

        private SyncEngine CreateEngine(
            ILocalFileScanner scanner,
            RemoteTreeSnapshot remoteTree,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            ILogger<SyncEngine>? logger = null,
            IRemoteFilePlaceholderWriter? remoteFilePlaceholderWriter = null)
        {
            return CreateEngineWithLogger(scanner, remoteFiles, out stateStore, logger, remoteFilePlaceholderWriter, remoteTree);
        }


        private SyncEngine CreateEngine(
            ILocalFileScanner scanner,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            params RemoteTreeSnapshot[] remoteTrees)
        {
            return CreateEngineWithLogger(scanner, remoteFiles, out stateStore, null, null, remoteTrees);
        }


        private SyncEngine CreateEngineWithLogger(
            ILocalFileScanner scanner,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            ILogger<SyncEngine>? logger,
            IRemoteFilePlaceholderWriter? remoteFilePlaceholderWriter,
            params RemoteTreeSnapshot[] remoteTrees)
        {
            stateStore = new SqliteSyncStateStore(_databasePath);
            return new SyncEngine(
                scanner,
                new FakeRemoteTreeCrawler(remoteTrees),
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: remoteFilePlaceholderWriter,
                logger: logger);
        }


        private SyncEngine CreateEngine(
            ILocalFileScanner scanner,
            RemoteTreeSnapshot remoteTree,
            FakeRemoteFileSynchronizer remoteFiles,
            out SqliteSyncStateStore stateStore,
            FakeRemoteDirectorySynchronizer remoteDirectories,
            ILogger<SyncEngine>? logger = null)
        {
            stateStore = new SqliteSyncStateStore(_databasePath);
            return new SyncEngine(
                scanner,
                new FakeRemoteTreeCrawler(remoteTree),
                remoteFiles,
                stateStore,
                remoteDirectories: remoteDirectories,
                logger: logger);
        }


        private SyncPair Pair(SyncPairMaterializationMode materializationMode = SyncPairMaterializationMode.FullMirror)
        {
            return new SyncPair
            {
                SyncPairId = "pair-a",
                LocalRootPath = _root,
                RemoteRootNodeId = _remoteRootNodeId,
                MaterializationMode = materializationMode,
            };
        }


        private async Task InsertBaselineAsync(
            SqliteSyncStateStore stateStore,
            string relativePath,
            string localContentHash,
            NodeFileManifestDto remoteFile,
            long? localSizeBytes = null)
        {
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = localContentHash,
                LocalLastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
                LocalSizeBytes = localSizeBytes,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = remoteFile.Id,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
        }


        private async Task InsertPlaceholderBaselineAsync(
            SqliteSyncStateStore stateStore,
            string relativePath,
            NodeFileManifestDto remoteFile,
            SyncPlaceholderHydrationState hydrationState = SyncPlaceholderHydrationState.RemoteOnly)
        {
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = remoteFile.Id,
                RemoteSizeBytes = remoteFile.SizeBytes,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                PlaceholderHydrationState = hydrationState,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
        }


        private async Task InsertDirectoryBaselineAsync(
            SqliteSyncStateStore stateStore,
            string relativePath,
            NodeDto remoteNode)
        {
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = remoteNode.Id,
                SyncedAtUtc = new DateTime(2026, 6, 2, 13, 1, 0, DateTimeKind.Utc),
            });
        }


        private LocalFileSnapshot LocalFile(string relativePath, string content)
        {
            return new LocalFileSnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                FullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                ContentHash = HashText(content),
                SizeBytes = Encoding.UTF8.GetByteCount(content),
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc),
            };
        }


        private LocalFileSnapshot CloudFilesPlaceholderLocal(string relativePath, long sizeBytes)
        {
            return new LocalFileSnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                FullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                ContentHash = string.Empty,
                SizeBytes = sizeBytes,
                LastWriteUtc = new DateTime(2026, 6, 2, 13, 2, 0, DateTimeKind.Utc),
                IsCloudFilesPlaceholder = true,
                IsCloudFilesOnlineOnlyPlaceholder = true,
            };
        }


        private LocalDirectorySnapshot LocalDirectory(string relativePath)
        {
            return new LocalDirectorySnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                FullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            };
        }


        private void WriteFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetLastWriteTimeUtc(fullPath, new DateTime(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc));
        }


        private LocalFileSnapshot? CreateMatrixLocal(string relativePath, MatrixFileState state, string content)
        {
            if (state == MatrixFileState.Missing)
            {
                return null;
            }

            WriteFile(relativePath, content);
            return LocalFile(relativePath, content);
        }


        private void AssertMatrixSideEffects(
            string relativePath,
            MatrixFileState localState,
            MatrixFileState remoteState,
            FakeRemoteFileSynchronizer remoteFiles)
        {
            string fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (localState == MatrixFileState.Missing && remoteState == MatrixFileState.Baseline)
            {
                Assert.That(remoteFiles.Deletes, Has.Count.EqualTo(1));
            }
            else if (localState == MatrixFileState.Baseline && remoteState == MatrixFileState.Missing)
            {
                Assert.That(File.Exists(fullPath), Is.False);
            }
            else if (localState == MatrixFileState.Baseline && remoteState == MatrixFileState.Changed)
            {
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("remote-changed"));
            }
            else if (localState == MatrixFileState.Changed && remoteState is MatrixFileState.Missing or MatrixFileState.Baseline)
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
            }
            else if (localState == MatrixFileState.Changed && remoteState == MatrixFileState.Changed)
            {
                string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("local-changed"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-changed"));
            }
            else if (localState == MatrixFileState.Missing && remoteState == MatrixFileState.Changed)
            {
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("remote-changed"));
            }
        }


        private RemoteTreeSnapshot EmptyRemoteTree()
        {
            return new RemoteTreeSnapshot
            {
                RootNode = new NodeDto
                {
                    Id = _remoteRootNodeId,
                    Name = "root",
                },
            };
        }


        private RemoteTreeSnapshot RemoteTree(params NodeFileManifestDto[] files)
        {
            RemoteTreeSnapshot tree = EmptyRemoteTree();
            foreach (NodeFileManifestDto file in files)
            {
                tree.Files.Add(new RemoteFileSnapshot
                {
                    RelativePath = file.Metadata["relativePath"],
                    File = file,
                });
            }

            return tree;
        }


        private RemoteDirectorySnapshot RemoteDirectory(string relativePath, Guid? parentNodeId = null)
        {
            return new RemoteDirectorySnapshot
            {
                RelativePath = relativePath.Replace('\\', '/'),
                Node = new NodeDto
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentNodeId ?? _remoteRootNodeId,
                    Name = relativePath.Split('/')[^1],
                },
            };
        }


        private NodeFileManifestDto RemoteFile(string relativePath, string contentHash, Guid? id = null, long sizeBytes = 1)
        {
            return new NodeFileManifestDto
            {
                Id = id ?? Guid.NewGuid(),
                CreatedAt = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 6, 2, 12, 30, 0, DateTimeKind.Utc),
                NodeId = _remoteRootNodeId,
                FileManifestId = Guid.NewGuid(),
                OriginalNodeFileId = id ?? Guid.NewGuid(),
                OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = relativePath.Split('/')[^1],
                ContentType = "text/plain",
                SizeBytes = sizeBytes,
                ContentHash = contentHash,
                ETag = "sha256-" + contentHash,
                Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath.Replace('\\', '/') },
            };
        }


        private static string HashText(string text)
        {
            return Hash(Encoding.UTF8.GetBytes(text));
        }


        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
    }
}
