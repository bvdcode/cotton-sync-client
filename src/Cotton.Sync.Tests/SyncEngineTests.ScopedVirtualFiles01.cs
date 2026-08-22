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
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRemoteOnlyPlaceholderChurnDoesNotRequireAction()
        {
            const string relativePath = "remote-only.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileScanner scanner = new LocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(scanner, crawler, remoteFiles, stateStore, remoteFilePlaceholderWriter: placeholderWriter);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(result.Activities, Is.Empty);
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests, Is.Empty);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesLocalDeletedRemoteOnlyPlaceholderDeletesRemote()
        {
            const string relativePath = "remote-only-deleted.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new();
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, relativePath, remote);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath], [relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.Deletes, Is.EqualTo(new[] { (remote.Id, false, remote.ETag) }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.DeletedRemote }));
                Assert.That(entry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesLocalDeletedRemoteOnlyPlaceholdersHonorsRemoteDeleteLimit()
        {
            const string firstPath = "remote-only-deleted-a.txt";
            const string secondPath = "remote-only-deleted-b.txt";
            NodeFileManifestDto firstRemote = RemoteFile(firstPath, HashText("remote-a"), sizeBytes: 1024);
            NodeFileManifestDto secondRemote = RemoteFile(secondPath, HashText("remote-b"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new();
            PathOnlyRemoteTreeCrawler crawler = new(RemoteTree(firstRemote, secondRemote));
            FakeRemoteFileSynchronizer remoteFiles = new();
            SqliteSyncStateStore stateStore = new(_databasePath);
            await InsertPlaceholderBaselineAsync(stateStore, firstPath, firstRemote);
            await InsertPlaceholderBaselineAsync(stateStore, secondPath, secondRemote);
            SyncEngine engine = new(scanner, crawler, remoteFiles, stateStore);

            SyncRunResult blockedResult = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    Scope = SyncRunScope.ForLocalChangedPaths([firstPath, secondPath], [firstPath, secondPath]),
                });

            const string fingerprintMarker = "Plan fingerprint ";
            string blockedDetails = blockedResult.Activities[0].Details!;
            int fingerprintStart = blockedDetails.IndexOf(fingerprintMarker, StringComparison.Ordinal)
                + fingerprintMarker.Length;
            string planFingerprint = blockedDetails.Substring(fingerprintStart, 64);
            char differentFingerprintCharacter = planFingerprint[0] == '0' ? '1' : '0';

            SyncRunResult changedPlanResult = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    ApprovedRemoteDeletePlan = new RemoteDeletePlanApproval(
                        2,
                        new string(differentFingerprintCharacter, 64)),
                    Scope = SyncRunScope.ForLocalChangedPaths([firstPath, secondPath], [firstPath, secondPath]),
                });

            SyncStateEntry? firstEntry = await stateStore.GetAsync("pair-a", firstPath);
            SyncStateEntry? secondEntry = await stateStore.GetAsync("pair-a", secondPath);
            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.Deletes, Is.Empty);
                Assert.That(blockedResult.RequiresUserAction, Is.True);
                Assert.That(changedPlanResult.RequiresUserAction, Is.True);
                Assert.That(blockedResult.Activities.Select(activity => activity.Kind), Is.EqualTo(new[]
                {
                    SyncActivityKind.Skipped,
                    SyncActivityKind.Skipped,
                }));
                Assert.That(blockedResult.Activities.Select(activity => activity.RequiresUserAction), Is.All.True);
                Assert.That(blockedResult.Activities[0].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(blockedResult.Activities[1].Details, Does.Contain("2 pending deletes exceed limit 1"));
                Assert.That(firstEntry, Is.Not.Null);
                Assert.That(secondEntry, Is.Not.Null);
            });

            SyncRunResult approvedResult = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions
                {
                    MaximumRemoteDeletesPerRun = 1,
                    ApprovedRemoteDeletePlan = new RemoteDeletePlanApproval(2, planFingerprint),
                    Scope = SyncRunScope.ForLocalChangedPaths([firstPath, secondPath], [firstPath, secondPath]),
                });

            firstEntry = await stateStore.GetAsync("pair-a", firstPath);
            secondEntry = await stateStore.GetAsync("pair-a", secondPath);
            Assert.Multiple(() =>
            {
                Assert.That(approvedResult.RequiresUserAction, Is.False);
                Assert.That(remoteFiles.Deletes, Has.Count.EqualTo(2));
                Assert.That(firstEntry, Is.Null);
                Assert.That(secondEntry, Is.Null);
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRemoteCreateUsesPathLookupAndCreatesPlaceholder()
        {
            const string relativePath = "remote-created.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner();
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ScanCalls, Is.Zero);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(crawler.FullCrawlCalls, Is.Zero);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRepairsPlaceholderWhoseBaselineWasInterrupted()
        {
            const string relativePath = "interrupted-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt;
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.Zero);
                Assert.That(crawler.PathCrawlCalls, Is.EqualTo(1));
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.RemoteContentHash, Is.EqualTo(remote.ContentHash));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }


        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRefreshesInterruptedPlaceholderMetadataWithoutHashing()
        {
            const string relativePath = "interrupted-placeholder.txt";
            NodeFileManifestDto remote = RemoteFile(relativePath, HashText("remote-content"), sizeBytes: 1024);
            LocalFileSnapshot local = CloudFilesPlaceholderLocal(relativePath, remote.SizeBytes);
            local.LastWriteUtc = remote.UpdatedAt.AddMinutes(-5);
            FakeLocalFileScanner scanner = new FakeLocalFileScanner(local);
            PathOnlyRemoteTreeCrawler crawler = new PathOnlyRemoteTreeCrawler(RemoteTree(remote));
            FakeRemoteFileSynchronizer remoteFiles = new FakeRemoteFileSynchronizer();
            FakeRemoteFilePlaceholderWriter placeholderWriter = new FakeRemoteFilePlaceholderWriter();
            SqliteSyncStateStore stateStore = new SqliteSyncStateStore(_databasePath);
            SyncEngine engine = new SyncEngine(
                scanner,
                crawler,
                remoteFiles,
                stateStore,
                remoteFilePlaceholderWriter: placeholderWriter);

            SyncRunResult result = await engine.RunOnceAsync(
                Pair(SyncPairMaterializationMode.WindowsVirtualFiles),
                new SyncRunOptions { Scope = SyncRunScope.ForLocalChangedPaths([relativePath]) });

            SyncStateEntry? entry = await stateStore.GetAsync("pair-a", relativePath);
            Assert.Multiple(() =>
            {
                Assert.That(scanner.ContentHashCalls, Is.Zero);
                Assert.That(remoteFiles.DownloadCalls, Is.Empty);
                Assert.That(remoteFiles.Uploads, Is.Empty);
                Assert.That(placeholderWriter.Requests.Select(request => request.RelativePath), Is.EqualTo(new[] { relativePath }));
                Assert.That(result.RequiresUserAction, Is.False);
                Assert.That(result.Activities.Select(activity => activity.Kind), Is.EqualTo(new[] { SyncActivityKind.PlaceholderCreated }));
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.RemoteFileId, Is.EqualTo(remote.Id));
                Assert.That(entry.PlaceholderIdentity, Is.EqualTo(placeholderWriter.PlaceholderIdentity));
                Assert.That(entry.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }
    }
}
