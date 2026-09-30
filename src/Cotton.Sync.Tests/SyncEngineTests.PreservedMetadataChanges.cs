// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [Test]
        public async Task RunOnceAsync_FullReconcileWithObservedVirtualFileDeleteDoesNotRestoreIt()
        {
            const string relativePath = "removed.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote"), sizeBytes: 6);
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeRemoteFilePlaceholderWriter placeholders = new();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles,
                out SqliteSyncStateStore stateStore, remoteFilePlaceholderWriter: placeholders);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    Scope = SyncRunScope.ForFull([relativePath], [relativePath], []),
                    RestoreMissingRemoteOnlyPlaceholders = true,
                });

            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(placeholders.Requests, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
            });
            Assert.That(await stateStore.GetAsync("pair-a", relativePath), Is.Null);
        }

        [TestCase(false, "Docs/report.txt")]
        [TestCase(true, "Docs/report.txt")]
        [TestCase(true, "Docs")]
        public async Task RunOnceAsync_ObservedEditPreservesSizeAndTimeStillUploads(bool full, string changedPath)
        {
            const string relativePath = "Docs/report.txt";
            WriteFile(relativePath, "old");
            string fullPath = Path.Combine(_root, "Docs", "report.txt");
            DateTime timestamp = File.GetLastWriteTimeUtc(fullPath);
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("old"), sizeBytes: 3);
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(new LocalFileScanner(), RemoteTree(remote), remoteFiles,
                out SqliteSyncStateStore stateStore);
            await stateStore.InitializeAsync();
            await stateStore.UpsertAsync(new SyncStateEntry
            {
                SyncPairId = "pair-a",
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                LocalContentHash = remote.ContentHash,
                LocalLastWriteUtc = timestamp,
                LocalSizeBytes = 3,
                RemoteNodeId = remote.NodeId,
                RemoteFileId = remote.Id,
                RemoteSizeBytes = remote.SizeBytes,
                RemoteContentHash = remote.ContentHash,
                RemoteETag = remote.ETag,
                SyncedAtUtc = DateTime.UtcNow,
            });
            WriteFile(relativePath, "new");
            File.SetLastWriteTimeUtc(fullPath, timestamp);
            SyncRunScope scope = SyncRunScope.ForLocalChangedPaths([changedPath]);
            if (full)
            {
                scope = SyncRunScope.ForFull([changedPath], [], []);
            }

            await engine.RunOnceAsync(Pair(), new SyncRunOptions { Scope = scope });

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads[0].ExistingRemoteFile!.Id, Is.EqualTo(remote.Id));
                Assert.That(remoteFiles.Uploads[0].LocalFile.ContentHash, Is.EqualTo(HashText("new")));
                Assert.That(state!.LocalContentHash, Is.EqualTo(HashText("new")));
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo("new"));
                Assert.That(new FileInfo(fullPath).Length, Is.EqualTo(3));
                Assert.That(File.GetLastWriteTimeUtc(fullPath), Is.EqualTo(timestamp));
            });
        }
    }
}
