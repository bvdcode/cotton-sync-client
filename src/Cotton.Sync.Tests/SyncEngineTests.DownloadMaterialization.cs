// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_VirtualFileDownloadUsesMaterializationLifecycle(bool failDownload)
        {
            const string relativePath = "changed-down.txt";
            WriteFile(relativePath, "old");
            LocalFileSnapshot local = LocalFile(relativePath, "old");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            if (failDownload)
            {
                remoteFiles.DownloadFailureIds.Add(remote.Id);
            }
            FakeRemoteFilePlaceholderWriter observer = new();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles,
                out SqliteSyncStateStore stateStore, remoteFilePlaceholderWriter: observer);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));
            if (failDownload)
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles)));
            }
            else
            {
                await engine.RunOnceAsync(Pair(SyncPairMaterializationMode.WindowsVirtualFiles));
            }
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(observer.FileMaterializationRequests, Has.Count.EqualTo(1));
                Assert.That(observer.FileExistsWhenMaterializationRequested, Is.EqualTo(new[] { true }));
                Assert.That(observer.CompletedFileMaterializationRequests, Has.Count.EqualTo(failDownload ? 0 : 1));
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo(failDownload ? "old" : "remote-new"));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(failDownload ? local.ContentHash : remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(entry.LocalContentHash));
                Assert.That(remoteFiles.Uploads, Is.Empty);
            });
        }
    }
}
