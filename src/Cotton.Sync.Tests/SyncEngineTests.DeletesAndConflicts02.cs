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
        public async Task RunOnceAsync_PreservesBothVersionsWhenNearSimultaneousLocalAndRemoteEditsDiverge()
        {
            string relativePath = "near-simultaneous-conflict.txt";
            Guid remoteId = Guid.NewGuid();
            string baselineContent = "baseline";
            DateTime baselineUtc = new(2026, 6, 2, 13, 0, 0, DateTimeKind.Utc);
            DateTime localEditUtc = baselineUtc.AddSeconds(1);
            DateTime remoteEditUtc = baselineUtc.AddSeconds(3);
            WriteFile(relativePath, "local-within-window");
            LocalFileSnapshot local = LocalFile(relativePath, "local-within-window");
            local.LastWriteUtc = localEditUtc;
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-within-window");
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText(baselineContent), remoteId, baselineContent.Length);
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), remoteId, remoteContent.Length);
            remote.UpdatedAt = remoteEditUtc;
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText(baselineContent), baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That((remoteEditUtc - localEditUtc).TotalSeconds, Is.EqualTo(2));
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-within-window"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-within-window"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalLastWriteUtc, Is.EqualTo(localEditUtc));
                Assert.That(entry.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.LocalContentHash, Is.Not.EqualTo(entry.RemoteContentHash));
            });
        }


        [TestCase(MatrixFileState.Missing, MatrixFileState.Missing, 0)]
        [TestCase(MatrixFileState.Missing, MatrixFileState.Baseline, (int)SyncActivityKind.DeletedRemote)]
        [TestCase(MatrixFileState.Missing, MatrixFileState.Changed, (int)SyncActivityKind.Conflict)]
        [TestCase(MatrixFileState.Baseline, MatrixFileState.Missing, (int)SyncActivityKind.DeletedLocal)]
        [TestCase(MatrixFileState.Baseline, MatrixFileState.Baseline, 0)]
        [TestCase(MatrixFileState.Baseline, MatrixFileState.Changed, (int)SyncActivityKind.Downloaded)]
        [TestCase(MatrixFileState.Changed, MatrixFileState.Missing, (int)SyncActivityKind.Conflict)]
        [TestCase(MatrixFileState.Changed, MatrixFileState.Baseline, (int)SyncActivityKind.Uploaded)]
        [TestCase(MatrixFileState.Changed, MatrixFileState.Changed, (int)SyncActivityKind.Conflict)]
        public async Task RunOnceAsync_ReconcilesBaselineMatrix(
            MatrixFileState localState,
            MatrixFileState remoteState,
            int expectedActivityKind)
        {
            string relativePath = $"matrix/{localState}-{remoteState}.txt";
            string baselineContent = "base";
            string localContent = localState == MatrixFileState.Changed ? "local-changed" : baselineContent;
            string remoteContent = remoteState == MatrixFileState.Changed ? "remote-changed" : baselineContent;
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText(baselineContent), remoteId);
            LocalFileSnapshot? local = CreateMatrixLocal(relativePath, localState, localContent);
            NodeFileManifestDto? remote = remoteState == MatrixFileState.Missing
                ? null
                : RemoteFile(relativePath, HashText(remoteContent), remoteId, Encoding.UTF8.GetByteCount(remoteContent));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            if (remote is not null && remoteState == MatrixFileState.Changed)
            {
                remoteFiles.Downloads[remote.Id] = Encoding.UTF8.GetBytes(remoteContent);
            }

            LocalFileSnapshot[] localFiles = local is null ? [] : [local];
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(localFiles),
                remote is null ? EmptyRemoteTree() : RemoteTree(remote),
                remoteFiles,
                out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, HashText(baselineContent), baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            IReadOnlyList<SyncActivityKind> expectedKinds = expectedActivityKind == 0
                ? []
                : [(SyncActivityKind)expectedActivityKind];
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(expectedKinds));
                AssertMatrixSideEffects(relativePath, localState, remoteState, remoteFiles);
            });
        }


        [Test]
        public async Task RunOnceAsync_PreservesBothVersionsWhenStaleUploadLosesRemoteRace()
        {
            string relativePath = "stale-upload.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto initialRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            byte[] latestRemoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto latestRemote = RemoteFile(relativePath, Hash(latestRemoteContent), remoteId, latestRemoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedUploadIds.Add(remoteId);
            remoteFiles.Downloads[remoteId] = latestRemoteContent;
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(initialRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(latestRemote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_FailsBeforeRaceConflictDownloadWhenRemoteVersionExceedsFreeSpace()
        {
            string relativePath = "stale-huge-upload.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto initialRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto latestRemote = RemoteFile(relativePath, HashText("remote-huge"), remoteId, long.MaxValue);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedUploadIds.Add(remoteId);
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(local),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(initialRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote);

            LocalInsufficientDiskSpaceException? exception = Assert.ThrowsAsync<LocalInsufficientDiskSpaceException>(
                () => engine.RunOnceAsync(Pair()));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Message, Does.Contain("Not enough disk space"));
                Assert.That(exception?.RelativePath, Does.Contain("stale-huge-upload"));
                Assert.That(exception?.RequiredBytes, Is.EqualTo(long.MaxValue));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories), Is.Empty);
            });
        }


        [Test]
        public async Task RunOnceAsync_RestoresRemoteVersionWhenStaleDeleteLosesRemoteRace()
        {
            string relativePath = "stale-delete.txt";
            Guid remoteId = Guid.NewGuid();
            NodeFileManifestDto baselineRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            NodeFileManifestDto initialRemote = RemoteFile(relativePath, HashText("old"), remoteId);
            byte[] latestRemoteContent = Encoding.UTF8.GetBytes("remote-new");
            NodeFileManifestDto latestRemote = RemoteFile(relativePath, Hash(latestRemoteContent), remoteId, latestRemoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.PreconditionFailedDeleteIds.Add(remoteId);
            remoteFiles.Downloads[remoteId] = latestRemoteContent;
            SyncEngine engine = CreateEngine(
                new FakeLocalFileScanner(),
                remoteFiles,
                out SqliteSyncStateStore stateStore,
                RemoteTree(initialRemote),
                RemoteTree(latestRemote));
            await InsertBaselineAsync(stateStore, relativePath, baselineRemote.ContentHash, baselineRemote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("remote-new"));
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remoteId, false, initialRemote.ETag) }));
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.LocalContentHash, Is.EqualTo(latestRemote.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(latestRemote.ContentHash));
            });
        }


        [Test]
        public async Task RunOnceAsync_DoesNotDuplicateConflictCopiesWhenUnresolvedConflictIsUnchanged()
        {
            string relativePath = "conflict-stable.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-new"));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, remote);

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(conflictFiles, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
            });
        }


        [Test]
        public async Task RunOnceAsync_PreservesUnresolvedConflictWhenRemoteChangesAgain()
        {
            string relativePath = "conflict-remote-again.txt";
            WriteFile(relativePath, "local-new");
            LocalFileSnapshot local = LocalFile(relativePath, "local-new");
            byte[] remoteContent = Encoding.UTF8.GetBytes("remote-newer");
            NodeFileManifestDto remote = RemoteFile(relativePath, Hash(remoteContent), sizeBytes: remoteContent.Length);
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            remoteFiles.Downloads[remote.Id] = remoteContent;
            SyncEngine engine = CreateEngine(new FakeLocalFileScanner(local), RemoteTree(remote), remoteFiles, out SqliteSyncStateStore stateStore);
            await InsertBaselineAsync(stateStore, relativePath, local.ContentHash, RemoteFile(relativePath, HashText("remote-old"), remote.Id));

            SyncRunResult result = await engine.RunOnceAsync(Pair());

            string[] conflictFiles = Directory.GetFiles(_root, "*Cotton conflict*", SearchOption.AllDirectories);
            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, relativePath)), Is.EqualTo("local-new"));
                Assert.That(conflictFiles, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(conflictFiles[0]), Is.EqualTo("remote-newer"));
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(result.Activities.Select(x => x.Kind), Is.EqualTo(new[] { SyncActivityKind.Conflict }));
                Assert.That(entry!.LocalContentHash, Is.EqualTo(local.ContentHash));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
            });
        }
    }
}
