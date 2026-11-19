// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_RefreshesCloudIdentityAfterRemoteDeleteAndReupload(bool sameContent)
        {
            const string relativePath = "Projects/report.zip";
            NodeFileManifestDto original = RemoteFile(relativePath, HashText("original"), sizeBytes: 1024);
            NodeFileManifestDto replacement = RemoteFile(relativePath,
                HashText(sameContent ? "original" : "replacement"), sizeBytes: 1024);
            FakeRemoteFilePlaceholderWriter placeholders = new();
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(CloudFilesPlaceholderLocal(relativePath, original.SizeBytes)),
                RemoteTree(replacement), remoteFiles, out SqliteSyncStateStore stateStore,
                remoteFilePlaceholderWriter: placeholders);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, original);

            SyncRunResult result = await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));

            SyncStateEntry? state = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(placeholders.Requests, Has.Count.EqualTo(1));
                Assert.That(placeholders.Requests.Single().RemoteFile.Id, Is.EqualTo(replacement.Id));
                Assert.That(placeholders.Requests.Single().ExistingHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(state?.RemoteFileId, Is.EqualTo(replacement.Id));
                Assert.That(state?.RemoteContentHash, Is.EqualTo(replacement.ContentHash));
                Assert.That(state?.PlaceholderIdentity, Is.EqualTo(placeholders.PlaceholderIdentity));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
            });
        }
    }
}
