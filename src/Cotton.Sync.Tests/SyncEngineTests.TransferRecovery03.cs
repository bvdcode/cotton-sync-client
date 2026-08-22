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

        [Test]
        public async Task RunOnceAsync_RejectsDownloadedContentThatDoesNotMatchManifest()
        {
            string relativePath = "download-corrupt.txt";
            byte[] expectedContent = Encoding.UTF8.GetBytes("complete remote file");
            byte[] partialContent = Encoding.UTF8.GetBytes("partial");
            NodeFileManifestDto remote = RemoteFile(
                relativePath,
                Hash(expectedContent),
                sizeBytes: expectedContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = partialContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);

            InvalidDataException? exception = Assert.ThrowsAsync<InvalidDataException>(
                async () => await engine.RunOnceAsync(Pair()));

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            string temporaryDirectory = Path.Combine(_root, ".cotton-sync", "tmp");
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(entry, Is.Null);
                Assert.That(
                    Directory.Exists(temporaryDirectory)
                        ? Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
                        : [],
                    Is.Empty);
            });
        }


        [Test]
        public async Task RunOnceAsync_RecoversAfterRemoteDownloadBeforeBaselineUpdate()
        {
            string relativePath = "downloaded-before-baseline.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SqliteSyncStateStore durableStore = new SqliteSyncStateStore(_databasePath);
            FailingUpsertStateStore failingStore = new FailingUpsertStateStore(durableStore);
            SyncEngine firstRun = new(
                new FakeLocalFileScanner(),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                failingStore);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await firstRun.RunOnceAsync(Pair()));

            IReadOnlyList<SyncStateEntry> entriesAfterCrash = await durableStore.LoadPairAsync("pair-a");
            LocalFileSnapshot downloadedLocal = LocalFile(relativePath, "remote-new");
            SyncEngine secondRun = new(
                new FakeLocalFileScanner(downloadedLocal),
                new FakeRemoteTreeCrawler(RemoteTree(remote)),
                remoteFiles,
                durableStore);

            SyncRunResult result = await secondRun.RunOnceAsync(Pair());

            SyncStateEntry? entry = await durableStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-new"));
                Assert.That(entriesAfterCrash, Is.Empty);
                Assert.That(result.Activities, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteFileId, Is.EqualTo(remote.Id));
            });
        }
    }
}
