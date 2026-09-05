// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_WithDownloadedActivityFinalizesPlaceholderInSamePass(bool scoped)
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/report.txt", SyncActivityKind.Downloaded);
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            RecordingCloudFilesAdapter cloudFiles = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(inner, activityPublisher, stateStore, cloudFiles);
            SyncRunRequest request = scoped ? SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]) : SyncRunRequest.Full;

            await work.RunOnceAsync(syncPair, request);

            SyncStateEntry? state = await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/report.txt");
            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(cloudFiles.DirectoryPlaceholders.Select(static item => item.RelativePath), Is.EqualTo(new[] { "Docs" }));
                Assert.That(cloudFiles.SyncRootInSyncPairs, Is.EqualTo(new[] { syncPair }));
                Assert.That(state!.PlaceholderIdentity, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
            });
        }

        [TestCase("downloaded-hash", "downloaded-hash", true)]
        [TestCase("DOWNLOADED-HASH", "downloaded-hash", true)]
        [TestCase("local-conflict-hash", "remote-conflict-hash", false)]
        [TestCase(null, "remote-conflict-hash", false)]
        [TestCase("local-conflict-hash", null, false)]
        [TestCase(null, null, false)]
        [TestCase("", "", false)]
        [TestCase(" ", " ", false)]
        public async Task RunOnceAsync_WithConflictFinalizesOnlyFileWhoseBaselineHashesMatch(
            string? localHash,
            string? remoteHash,
            bool shouldFinalize)
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/report.txt", SyncActivityKind.Conflict);
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            SyncStateEntry state = (await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/report.txt"))!;
            state.LocalContentHash = localHash;
            state.RemoteContentHash = remoteHash;
            RecordingCloudFilesAdapter cloudFiles = new();
            RecordingRunProgressPublisher progress = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(
                inner, activityPublisher, stateStore, cloudFiles, runProgressPublisher: progress);

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.InSyncPaths.Contains("Docs/report.txt"), Is.EqualTo(shouldFinalize));
                Assert.That(cloudFiles.SyncRootInSyncPairs.Count, Is.EqualTo(shouldFinalize ? 1 : 0));
                Assert.That(state.PlaceholderIdentity is { Length: > 0 }, Is.EqualTo(shouldFinalize));
                Assert.That(progress.Progress.Count > 0, Is.EqualTo(shouldFinalize));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithDirectoryConflictDoesNotFinalizeEvenWhenHashesMatch()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs", SyncActivityKind.Conflict);
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            SyncStateEntry state = (await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs"))!;
            state.LocalContentHash = "same-hash";
            state.RemoteContentHash = "same-hash";
            RecordingCloudFilesAdapter cloudFiles = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(inner, activityPublisher, stateStore, cloudFiles);

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
                Assert.That(cloudFiles.DirectoryPlaceholders, Is.Empty);
                Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
            });
        }

        [TestCase(SyncActivityKind.Downloaded)]
        [TestCase(SyncActivityKind.Conflict)]
        public async Task RunOnceAsync_WhenDownloadedFileChangesBeforeFinalizationDoesNotMarkItInSync(
            SyncActivityKind activityKind)
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/report.txt", activityKind);
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            SyncStateEntry state = (await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/report.txt"))!;
            state.LocalContentHash = state.RemoteContentHash;
            LocalFileUnavailableException expectedException = new(
                "Docs/report.txt", Path.Combine(syncPair.LocalRootPath, "Docs", "report.txt"), "The file changed.");
            RecordingCloudFilesAdapter cloudFiles = new() { Exception = expectedException };
            WindowsVirtualFilesUploadFinalizationPairWork work = new(inner, activityPublisher, stateStore, cloudFiles);

            LocalFileUnavailableException? exception = Assert.ThrowsAsync<LocalFileUnavailableException>(
                () => work.RunOnceAsync(syncPair, SyncRunRequest.Full));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.SameAs(expectedException));
                Assert.That(state.PlaceholderIdentity, Is.Null);
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.None));
                Assert.That(cloudFiles.DirectoryPlaceholders, Is.Empty);
                Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
            });
        }
    }
}
