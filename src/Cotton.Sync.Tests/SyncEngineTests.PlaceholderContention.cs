// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_WhenPlaceholderNeedsExclusiveAccessPreservesStateUntilRetry(bool hasBaseline)
        {
            const string relativePath = "remote-updated.txt";
            Guid remoteFileId = Guid.NewGuid();
            NodeFileManifestDto original = RemoteFile(relativePath, HashText("old-content"), remoteFileId, sizeBytes: 1024);
            NodeFileManifestDto updated = RemoteFile(relativePath, HashText("new-content"), remoteFileId, sizeBytes: 2048);
            LocalFileUnavailableException unavailable = new(
                relativePath,
                Path.Combine(_root, relativePath),
                new IOException("The file is being used by another process."),
                requiresExclusiveAccess: true);
            FakeRemoteFilePlaceholderWriter placeholders = new() { Failure = unavailable };
            FakeRemoteFileSynchronizer remoteFiles = new();
            FakeLocalFileScanner scanner = hasBaseline
                ? new FakeLocalFileScanner(CloudFilesPlaceholderLocal(relativePath, original.SizeBytes))
                : new FakeLocalFileScanner();
            SyncEngine engine = CreateEngine(
                scanner,
                RemoteTree(updated),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholders);
            if (hasBaseline)
            {
                await InsertPlaceholderBaselineAsync(stateStore, relativePath, original);
            }

            SyncPair pair = Pair(SyncPairMaterializationMode.WindowsVirtualFiles);
            SyncRunOptions options = new() { AllowInitialVirtualFilesStreaming = false };
            LocalFileUnavailableException? failure = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => engine.RunOnceAsync(pair, options));
            SyncStateEntry? blockedState = await stateStore.GetAsync(pair.SyncPairId, relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.SameAs(unavailable));
                Assert.That(blockedState?.RemoteContentHash, Is.EqualTo(hasBaseline ? original.ContentHash : null));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });

            placeholders.Failure = null;
            SyncRunResult result = await engine.RunOnceAsync(pair, options);
            SyncStateEntry? recoveredState = await stateStore.GetAsync(pair.SyncPairId, relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(recoveredState?.RemoteFileId, Is.EqualTo(updated.Id));
                Assert.That(recoveredState?.RemoteContentHash, Is.EqualTo(updated.ContentHash));
                Assert.That(recoveredState?.RemoteSizeBytes, Is.EqualTo(updated.SizeBytes));
                Assert.That(placeholders.Requests, Has.Count.EqualTo(2));
            });
        }
    }
}
