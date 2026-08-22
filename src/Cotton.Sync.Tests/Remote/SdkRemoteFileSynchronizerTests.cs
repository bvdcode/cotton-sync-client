// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;
using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Chunks;
using Cotton.Sdk.Files;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Notifications;
using Cotton.Sdk.Realtime;
using Cotton.Sdk.Settings;
using Cotton.Sdk.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        private readonly Guid _rootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private string _root = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "cotton-sdk-remote-sync", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public async Task UploadFileAsync_CreatesFoldersUploadsMissingChunksAndCreatesFile()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdefghij");
            LocalFileSnapshot local = WriteLocalFile("Docs/Reports/file.txt", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            string firstChunkHash = Hash(Encoding.UTF8.GetBytes("abcd"));
            client.ChunksClient.ExistingHashes.Add(firstChunkHash);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);

            NodeFileManifestDto created = await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            Assert.Multiple(() =>
            {
                Assert.That(client.SettingsClient.Calls, Is.EqualTo(1));
                Assert.That(client.NodesClient.CreatedNodes.Select(x => x.Name), Is.EqualTo(new[] { "Docs", "Reports" }));
                Assert.That(client.ChunksClient.ExistsChecks, Has.Count.EqualTo(3));
                Assert.That(client.ChunksClient.UploadedChunks.Select(x => x.Hash), Is.EqualTo(client.ChunksClient.ExistsChecks.Skip(1)));
                Assert.That(client.FilesClient.CreateRequests, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.UpdateRequests, Is.Empty);
                Assert.That(client.FilesClient.CreateRequests[0].NodeId, Is.EqualTo(client.NodesClient.CreatedNodes[^1].Id));
                Assert.That(client.FilesClient.CreateRequests[0].Name, Is.EqualTo("file.txt"));
                Assert.That(client.FilesClient.CreateRequests[0].ContentType, Is.EqualTo("text/plain"));
                Assert.That(client.FilesClient.CreateRequests[0].Hash, Is.EqualTo(local.ContentHash));
                Assert.That(client.FilesClient.CreateRequests[0].Validate, Is.False);
                Assert.That(created.ContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task UploadFileAsync_ComputesContentHashFromChunkStreamWhenSnapshotHasNoHash()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdefghij");
            LocalFileSnapshot local = WriteLocalFile("Docs/file.txt", bytes);
            local.ContentHash = string.Empty;
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);

            NodeFileManifestDto created = await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            string expectedHash = Hash(bytes);
            Assert.Multiple(() =>
            {
                Assert.That(client.FilesClient.CreateRequests, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.CreateRequests[0].Hash, Is.EqualTo(expectedHash));
                Assert.That(created.ContentHash, Is.EqualTo(expectedHash));
            });
        }

        [Test]
        public async Task UploadFileAsync_RevalidatesCachedParentAfterRemoteFolderReplacement()
        {
            Guid originalParentId = Guid.NewGuid();
            Guid replacementParentId = Guid.NewGuid();
            LocalFileSnapshot first = WriteLocalFile("Docs/first.txt", Encoding.UTF8.GetBytes("first"));
            LocalFileSnapshot second = WriteLocalFile("Docs/second.txt", Encoding.UTF8.GetBytes("second"));
            FakeCottonCloudClient client = new(chunkSizeBytes: 1024);
            client.NodesClient.Children[_rootNodeId] = [Node(originalParentId, _rootNodeId, "Docs")];
            SdkRemoteFileSynchronizer synchronizer = new(client);

            await synchronizer.UploadFileAsync(_rootNodeId, first.RelativePath, first);
            client.NodesClient.Children[_rootNodeId] = [Node(replacementParentId, _rootNodeId, "Docs")];
            await synchronizer.UploadFileAsync(_rootNodeId, second.RelativePath, second);

            Assert.Multiple(() =>
            {
                Assert.That(client.NodesClient.GetRequests, Does.Contain(originalParentId));
                Assert.That(client.FilesClient.CreateRequests, Has.Count.EqualTo(2));
                Assert.That(client.FilesClient.CreateRequests[0].NodeId, Is.EqualTo(originalParentId));
                Assert.That(client.FilesClient.CreateRequests[1].NodeId, Is.EqualTo(replacementParentId));
            });
        }

        [Test]
        public async Task UploadFileAsync_ReusesParentCreatedByConcurrentRequest()
        {
            LocalFileSnapshot local = WriteLocalFile("Docs/file.txt", Encoding.UTF8.GetBytes("content"));
            FakeCottonCloudClient client = new(chunkSizeBytes: 1024);
            client.NodesClient.ConflictCreates.Add((_rootNodeId, "Docs"));
            SdkRemoteFileSynchronizer synchronizer = new(client);

            await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            NodeDto concurrentDirectory = client.NodesClient.Children[_rootNodeId].Single();
            Assert.Multiple(() =>
            {
                Assert.That(client.NodesClient.CreatedNodes, Is.Empty);
                Assert.That(concurrentDirectory.Name, Is.EqualTo("Docs"));
                Assert.That(client.FilesClient.CreateRequests, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.CreateRequests[0].NodeId, Is.EqualTo(concurrentDirectory.Id));
            });
        }

        [Test]
        public async Task UploadFileAsync_CreatesCanonicallyDistinctParentDirectory()
        {
            Guid parentId = Guid.NewGuid();
            LocalFileSnapshot local = WriteLocalFile("Michaël Brun/file.txt", Encoding.UTF8.GetBytes("content"));
            FakeCottonCloudClient client = new(chunkSizeBytes: 1024);
            client.NodesClient.Children[_rootNodeId] = [Node(parentId, _rootNodeId, "Michael Brun")];
            SdkRemoteFileSynchronizer synchronizer = new(client);

            await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            Assert.Multiple(() =>
            {
                Assert.That(client.NodesClient.CreatedNodes.Select(static node => node.Name), Is.EqualTo(new[] { "Michaël Brun" }));
                Assert.That(client.FilesClient.CreateRequests, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.CreateRequests[0].NodeId, Is.EqualTo(client.NodesClient.CreatedNodes[0].Id));
            });
        }

        [Test]
        public async Task UploadFileAsync_ReusesExistingFolderAndUpdatesExistingFile()
        {
            Guid docsId = Guid.NewGuid();
            byte[] bytes = Encoding.UTF8.GetBytes("updated");
            LocalFileSnapshot local = WriteLocalFile("Docs/file.bin", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 1024);
            client.NodesClient.Children[_rootNodeId] = [Node(docsId, _rootNodeId, "Docs")];
            NodeFileManifestDto existing = RemoteFile("file.bin", HashText("old"));
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);

            NodeFileManifestDto updated = await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local, existing);

            Assert.Multiple(() =>
            {
                Assert.That(client.NodesClient.CreatedNodes, Is.Empty);
                Assert.That(client.FilesClient.CreateRequests, Is.Empty);
                Assert.That(client.FilesClient.UpdateRequests, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.UpdateRequests[0].NodeFileId, Is.EqualTo(existing.Id));
                Assert.That(client.FilesClient.UpdateRequests[0].Request.NodeId, Is.EqualTo(docsId));
                Assert.That(client.FilesClient.UpdateRequests[0].Request.OriginalNodeFileId, Is.EqualTo(existing.OriginalNodeFileId));
                Assert.That(client.FilesClient.UpdateRequests[0].ExpectedETag, Is.EqualTo(existing.ETag));
                Assert.That(updated.Id, Is.EqualTo(existing.Id));
                Assert.That(updated.ContentHash, Is.EqualTo(local.ContentHash));
            });
        }

        [Test]
        public async Task UploadFileAsync_ServerErrorAfterChunkTransferPreservesRemoteFileUntilRetry()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("updated after retry");
            LocalFileSnapshot local = WriteLocalFile("file.bin", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 1024);
            NodeFileManifestDto existing = RemoteFile("file.bin", HashText("remote-before-server-error"));
            client.FilesClient.Files[existing.Id] = existing;
            client.FilesClient.UpdateContentFailuresRemaining = 1;
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);

            Assert.ThrowsAsync<CottonApiException>(
                async () => await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local, existing));

            NodeFileManifestDto remoteAfterFailure = client.FilesClient.Files[existing.Id];
            Assert.Multiple(() =>
            {
                Assert.That(remoteAfterFailure.ContentHash, Is.EqualTo(existing.ContentHash));
                Assert.That(remoteAfterFailure.ETag, Is.EqualTo(existing.ETag));
                Assert.That(client.ChunksClient.UploadedChunks, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.UpdateRequests, Has.Count.EqualTo(1));
            });

            NodeFileManifestDto recovered = await synchronizer.UploadFileAsync(
                _rootNodeId,
                local.RelativePath,
                local,
                existing);

            Assert.Multiple(() =>
            {
                Assert.That(recovered.ContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(client.FilesClient.Files[existing.Id].ContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(client.ChunksClient.UploadedChunks, Has.Count.EqualTo(1));
                Assert.That(client.FilesClient.UpdateRequests, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public async Task MoveFileAsync_MovesToExistingParentAndRenamesWithFreshETags()
        {
            Guid docsId = Guid.NewGuid();
            Guid reportsId = Guid.NewGuid();
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 1024);
            client.NodesClient.Children[_rootNodeId] = [Node(docsId, _rootNodeId, "Docs")];
            client.NodesClient.Children[docsId] = [Node(reportsId, docsId, "Reports")];
            NodeFileManifestDto existing = RemoteFile("old.txt", HashText("same"));
            existing.NodeId = _rootNodeId;
            existing.ETag = "sha256-original";
            client.FilesClient.Files[existing.Id] = existing;
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);

            NodeFileManifestDto moved = await synchronizer.MoveFileAsync(_rootNodeId, "Docs/Reports/new.txt", existing);

            Assert.Multiple(() =>
            {
                Assert.That(client.NodesClient.CreatedNodes, Is.Empty);
                Assert.That(client.FilesClient.MoveRequests, Is.EqualTo(new[] { (existing.Id, reportsId, "sha256-original") }));
                Assert.That(client.FilesClient.RenameRequests, Is.EqualTo(new[] { (existing.Id, "new.txt", "sha256-moved-1") }));
                Assert.That(moved.Id, Is.EqualTo(existing.Id));
                Assert.That(moved.NodeId, Is.EqualTo(reportsId));
                Assert.That(moved.Name, Is.EqualTo("new.txt"));
                Assert.That(moved.ETag, Is.EqualTo("sha256-renamed-1"));
            });
        }

        [Test]
        public async Task UploadFileAsync_UploadsEmptyFileAsEmptyChunk()
        {
            LocalFileSnapshot local = WriteLocalFile("empty.bin", []);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 8);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(client);

            await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            string emptyHash = Hash([]);
            Assert.Multiple(() =>
            {
                Assert.That(client.ChunksClient.ExistsChecks, Is.EqualTo(new[] { emptyHash }));
                Assert.That(client.ChunksClient.UploadedChunks, Has.Count.EqualTo(1));
                Assert.That(client.ChunksClient.UploadedChunks[0].Bytes, Is.Empty);
                Assert.That(client.FilesClient.CreateRequests[0].ChunkHashes, Is.EqualTo(new[] { emptyHash }));
            });
        }

        [Test]
        public async Task UploadFileAsync_ReportsChunkProgress()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdefghij");
            LocalFileSnapshot local = WriteLocalFile("Docs/file.txt", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(
                client,
                new SdkRemoteFileSynchronizerOptions { MaxConcurrentChunkUploads = 1 });
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();

            await synchronizer.UploadFileAsync(
                _rootNodeId,
                local.RelativePath,
                local,
                existingRemoteFile: null,
                transferProgress: progress);

            Assert.Multiple(() =>
            {
                Assert.That(progress.Values.Select(value => value.TransferredBytes), Is.EqualTo(new long[] { 0, 4, 8, 10, 10 }));
                Assert.That(progress.Values.Select(value => value.TotalBytes), Is.All.EqualTo(10));
                Assert.That(progress.Values.Select(value => value.Direction), Is.All.EqualTo(SyncTransferDirection.Upload));
                Assert.That(progress.Values.Select(value => value.RelativePath), Is.All.EqualTo("Docs/file.txt"));
                Assert.That(progress.Values[^1].IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task UploadFileAsync_ReportsChunkProgressBeforeWholeBatchCompletes()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdefghijkl");
            LocalFileSnapshot local = WriteLocalFile("Docs/file.txt", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            string firstChunkHash = Hash(Encoding.UTF8.GetBytes("abcd"));
            client.ChunksClient.BlockUpload(firstChunkHash);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(
                client,
                new SdkRemoteFileSynchronizerOptions { MaxConcurrentChunkUploads = 2 });
            SignalingProgress<SyncTransferProgress> progress = new SignalingProgress<SyncTransferProgress>(
                value => value.TransferredBytes > 0);

            Task upload = synchronizer.UploadFileAsync(
                _rootNodeId,
                local.RelativePath,
                local,
                existingRemoteFile: null,
                transferProgress: progress);
            SyncTransferProgress firstProgress = await progress.WaitForMatchAsync().ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(firstProgress.TransferredBytes, Is.EqualTo(4));
                Assert.That(firstProgress.TotalBytes, Is.EqualTo(12));
                Assert.That(upload.IsCompleted, Is.False);
            });

            client.ChunksClient.ReleaseUpload(firstChunkHash);
            await upload.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        [Test]
        public async Task UploadFileAsync_UsesBoundedChunkUploadConcurrency()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdefghijklmnop");
            LocalFileSnapshot local = WriteLocalFile("Docs/file.txt", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            client.ChunksClient.OperationDelay = TimeSpan.FromMilliseconds(25);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(
                client,
                new SdkRemoteFileSynchronizerOptions { MaxConcurrentChunkUploads = 2 });
            string[] expectedChunkHashes =
            [
                Hash(Encoding.UTF8.GetBytes("abcd")),
                Hash(Encoding.UTF8.GetBytes("efgh")),
                Hash(Encoding.UTF8.GetBytes("ijkl")),
                Hash(Encoding.UTF8.GetBytes("mnop")),
            ];

            await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            Assert.Multiple(() =>
            {
                Assert.That(client.ChunksClient.UploadedChunks, Has.Count.EqualTo(4));
                Assert.That(client.ChunksClient.MaxConcurrentOperations, Is.GreaterThan(1));
                Assert.That(client.ChunksClient.MaxConcurrentOperations, Is.LessThanOrEqualTo(2));
                Assert.That(client.FilesClient.CreateRequests.Single().ChunkHashes, Is.EqualTo(expectedChunkHashes));
                Assert.That(client.ChunksClient.ExistsChecks, Is.EquivalentTo(expectedChunkHashes));
            });
        }

        [Test]
        public async Task UploadFileAsync_DeduplicatesChunkNetworkWorkWithinOneFile()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdabcdabcd");
            LocalFileSnapshot local = WriteLocalFile("Docs/repeated.bin", bytes);
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(
                client,
                new SdkRemoteFileSynchronizerOptions { MaxConcurrentChunkUploads = 3 });
            string repeatedChunkHash = Hash(Encoding.UTF8.GetBytes("abcd"));

            await synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);

            Assert.Multiple(() =>
            {
                Assert.That(client.ChunksClient.ExistsChecks, Is.EqualTo(new[] { repeatedChunkHash }));
                Assert.That(client.ChunksClient.UploadedChunks.Select(chunk => chunk.Hash), Is.EqualTo(new[] { repeatedChunkHash }));
                Assert.That(
                    client.FilesClient.CreateRequests.Single().ChunkHashes,
                    Is.EqualTo(new[] { repeatedChunkHash, repeatedChunkHash, repeatedChunkHash }));
            });
        }

        [Test]
        public async Task UploadFileAsync_AllowsConcurrentWriterWhileReadingLocalFile()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abcdefgh");
            LocalFileSnapshot local = WriteLocalFile("Docs/concurrent.txt", bytes);
            local.ContentHash = string.Empty;
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);
            string firstChunkHash = Hash(Encoding.UTF8.GetBytes("abcd"));
            client.ChunksClient.BlockUpload(firstChunkHash);
            SdkRemoteFileSynchronizer synchronizer = new SdkRemoteFileSynchronizer(
                client,
                new SdkRemoteFileSynchronizerOptions { MaxConcurrentChunkUploads = 1 });

            Task<NodeFileManifestDto> upload = synchronizer.UploadFileAsync(_rootNodeId, local.RelativePath, local);
            await client.ChunksClient.WaitForUploadAttemptAsync(firstChunkHash).ConfigureAwait(false);

            Assert.DoesNotThrow(() =>
            {
                using FileStream writer = new(
                    local.FullPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                writer.SetLength(0);
                byte[] replacement = Encoding.UTF8.GetBytes("WXYZabcd");
                writer.Write(replacement, 0, replacement.Length);
            });

            client.ChunksClient.ReleaseUpload(firstChunkHash);
            await upload.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        [Test]
        public void Constructor_RejectsInvalidChunkUploadConcurrency()
        {
            FakeCottonCloudClient client = new FakeCottonCloudClient(chunkSizeBytes: 4);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SdkRemoteFileSynchronizer(
                    client,
                    new SdkRemoteFileSynchronizerOptions { MaxConcurrentChunkUploads = 0 }));
        }

    }
}
