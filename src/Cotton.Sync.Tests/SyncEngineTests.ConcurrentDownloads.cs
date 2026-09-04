// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text;
using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_PreservesLocalEditMadeDuringRemoteDownload(bool useAtomicSave)
        {
            const string relativePath = "concurrent-download.txt";
            const string originalContent = "original";
            const string changedContent = "modified";
            WriteFile(relativePath, originalContent);
            LocalFileSnapshot local = LocalFile(relativePath, originalContent);
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote version");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            remoteFiles.BeforeDownload = _ =>
            {
                if (useAtomicSave)
                {
                    WriteFile("editor-save.tmp", changedContent);
                    File.Move(Path.Combine(_root, "editor-save.tmp"), Path.Combine(_root, relativePath), overwrite: true);
                }
                else
                {
                    WriteFile(relativePath, changedContent);
                }

                File.SetLastWriteTimeUtc(Path.Combine(_root, relativePath), local.LastWriteUtc);
            };
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictPaths = Directory.GetFiles(_root, "* (Cotton conflict *).txt");
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote version"));
                Assert.That(conflictPaths, Has.Length.EqualTo(1));
                Assert.That(conflictPaths.Select(File.ReadAllText), Is.EqualTo(new[] { changedContent }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "tmp")), Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_DefersDownloadWhenLocalFileIsDeletedAfterScan()
        {
            const string relativePath = "deleted-during-download.txt";
            WriteFile(relativePath, "original");
            LocalFileSnapshot local = LocalFile(relativePath, "original");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote version");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            remoteFiles.BeforeDownload = _ => File.Delete(Path.Combine(_root, relativePath));
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, local.ContentHash, remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(_root, relativePath)), Is.False);
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { relativePath }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(local.ContentHash));
            });

            string preservationDirectory = Path.Combine(_root, ".cotton-sync", "deleted");
            if (OperatingSystem.IsLinux())
            {
                string[] preservedFiles = Directory.GetFiles(preservationDirectory, "*.txt", SearchOption.AllDirectories);
                Assert.That(preservedFiles.Select(File.ReadAllText), Is.EqualTo(new[] { "remote version" }));
            }
            else
            {
                Assert.That(Directory.GetFileSystemEntries(preservationDirectory), Is.Empty);
            }
        }

        [Test]
        public async Task RunOnceAsync_DefersDownloadWhenLocalFileAppearsAfterScan()
        {
            const string relativePath = "new-during-download.txt";
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote version");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            remoteFiles.BeforeDownload = _ => WriteFile(relativePath, "new local version");
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("new local version"));
                Assert.That(result.DeferredLocalPaths, Is.EqualTo(new[] { relativePath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Skipped }));
                Assert.That(entry, Is.Null);
                Assert.That(Directory.GetFiles(Path.Combine(_root, ".cotton-sync", "tmp")), Is.Empty);
            });

            remoteFiles.BeforeDownload = null;
            SyncEngine retry = CreateEngine(
                new FakeLocalFileScanner(LocalFile(relativePath, "new local version")),
                RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore retryStateStore);
            SyncRunResult retryResult = await retry.RunOnceAsync(Pair());
            SyncStateEntry? retryEntry = await retryStateStore.GetAsync("pair-a", relativePath);
            string[] conflictPaths = Directory.GetFiles(_root, "* (Cotton conflict *).txt");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("new local version"));
                Assert.That(conflictPaths.Select(File.ReadAllText), Is.EqualTo(new[] { "remote version" }));
                Assert.That(retryResult.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(retryResult.DeferredLocalPaths, Is.Empty);
                Assert.That(retryEntry!.LocalContentHash, Is.EqualTo(HashText("new local version")));
                Assert.That(retryEntry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }
    }
}
