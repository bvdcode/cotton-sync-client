// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {
        [TestCase(SyncPairMaterializationMode.FullMirror, false)]
        [TestCase(SyncPairMaterializationMode.FullMirror, true)]
        [TestCase(SyncPairMaterializationMode.WindowsVirtualFiles, false)]
        [TestCase(SyncPairMaterializationMode.WindowsVirtualFiles, true)]
        public async Task RunOnceAsync_RenameThroughTemporaryPathThenEdit_PreservesRemoteFileId(
            SyncPairMaterializationMode mode, bool fullReconcile)
        {
            const string sourcePath = "Scans/original.pdf";
            const string temporaryPath = "Scans/rename.tmp";
            const string targetPath = "Scans/final.pdf";
            const string originalContent = "original document";
            const string editedContent = "updated document with additional pages";
            WriteFile(sourcePath, originalContent);
            File.Move(Path.Combine(_root, sourcePath), Path.Combine(_root, temporaryPath));
            File.Move(Path.Combine(_root, temporaryPath), Path.Combine(_root, targetPath));
            await File.WriteAllTextAsync(Path.Combine(_root, targetPath), editedContent);
            LocalFileSnapshot local = LocalFile(targetPath, editedContent);
            NodeFileManifestDto original = RemoteFile(sourcePath, HashText(originalContent), sizeBytes: originalContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(original), remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, sourcePath, original.ContentHash, original, originalContent.Length);
            SyncPair pair = Pair(mode);
            SyncRunOptions options = new()
            {
                Scope = SyncRunScope.ForLocalChangedPaths([sourcePath, targetPath], [],
                    [new LocalPathRename(sourcePath, temporaryPath), new LocalPathRename(temporaryPath, targetPath)]),
            };
            if (fullReconcile)
            {
                options.Scope = SyncRunScope.ForFull(options.Scope.LocalRenames);
            }

            SyncRunResult result = await engine.RunOnceAsync(pair, options);

            SyncStateEntry? finalState = await stateStore.GetAsync("pair-a", targetPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Uploads, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads.Single().ExistingRemoteFile?.Id, Is.EqualTo(original.Id));
                Assert.That(finalState?.RemoteFileId, Is.EqualTo(original.Id));
                Assert.That(finalState?.RemoteOriginalNodeFileId, Is.EqualTo(original.OriginalNodeFileId));
                Assert.That(finalState?.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(finalState?.RemoteContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(result.RequiresUserAction, Is.False);
            });
            Assert.That(await stateStore.GetAsync("pair-a", sourcePath), Is.Null);
        }

        [TestCase(SyncPairMaterializationMode.FullMirror)]
        [TestCase(SyncPairMaterializationMode.WindowsVirtualFiles)]
        public async Task RunOnceAsync_RenamedFileUnavailable_DefersBothPathsAndPreservesIdOnRetry(
            SyncPairMaterializationMode mode)
        {
            const string sourcePath = "original.pdf";
            const string targetPath = "final.pdf";
            const string originalContent = "original document";
            const string editedContent = "edited document";
            WriteFile(targetPath, editedContent);
            LocalFileSnapshot local = LocalFile(targetPath, editedContent);
            local.ContentHash = string.Empty;
            bool unavailable = true;
            FakeLocalFileScanner scanner = new(local)
            {
                ContentHashFactory = file => unavailable
                    ? throw new LocalFileUnavailableException(file.RelativePath, file.FullPath, "File is being saved.")
                    : HashText(editedContent),
            };
            NodeFileManifestDto original = RemoteFile(sourcePath, HashText(originalContent), sizeBytes: originalContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new();
            SyncEngine engine = CreateEngine(scanner, RemoteTree(original), remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, sourcePath, original.ContentHash, original, originalContent.Length);
            SyncRunOptions options = new()
            {
                Scope = SyncRunScope.ForLocalChangedPaths([sourcePath, targetPath], [],
                    [new LocalPathRename(sourcePath, targetPath)]),
            };

            SyncRunResult deferred = await engine.RunOnceAsync(Pair(mode), options);

            Assert.Multiple(() =>
            {
                Assert.That(deferred.DeferredLocalPaths, Is.EquivalentTo(new[] { sourcePath, targetPath }));
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Moves, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
            });
            Assert.That((await stateStore.GetAsync("pair-a", sourcePath))?.RemoteFileId, Is.EqualTo(original.Id));

            unavailable = false;
            SyncRunResult retried = await engine.RunOnceAsync(Pair(mode), options);

            Assert.Multiple(() =>
            {
                Assert.That(retried.DeferredLocalPaths, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(remoteFiles.Moves, Has.Count.EqualTo(1));
                Assert.That(remoteFiles.Uploads.Single().ExistingRemoteFile?.Id, Is.EqualTo(original.Id));
            });
            Assert.That((await stateStore.GetAsync("pair-a", targetPath))?.RemoteFileId, Is.EqualTo(original.Id));
        }
    }
}
