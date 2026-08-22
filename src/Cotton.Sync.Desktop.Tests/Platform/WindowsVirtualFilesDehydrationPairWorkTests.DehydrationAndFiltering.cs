// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_DehydratesSafeUnpinnedPlaceholderAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => CreateUnpinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[] { new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/report.txt") }));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
                Assert.That(updated.LocalContentHash, Is.Null);
                Assert.That(updated.LocalLastWriteUtc, Is.Null);
                Assert.That(updated.LocalSizeBytes, Is.Null);
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-free-up-space"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_RecordsExplorerCompletedDehydrationWithoutInnerSyncOrSecondDehydrate()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = "remote-hash";
            state.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc);
            state.LocalSizeBytes = 12;
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("unexpected-hash"),
                diagnostics,
                _ => CreateUnpinnedRemoteOnlyDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Dehydrated));
                Assert.That(updated.LocalContentHash, Is.Null);
                Assert.That(updated.LocalLastWriteUtc, Is.Null);
                Assert.That(updated.LocalSizeBytes, Is.Null);
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-free-up-space"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_SuppressesInnerSyncForUnpinnedTrackedDirectoryPlaceholder()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                readDiskState: _ => CreateUnpinnedDirectoryDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Music"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_SuppressesNeutralHydratedDirectoryUnpinSubtree()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            SyncStateEntry fileState = CreatePlaceholderState(syncPair, "Music/track.mp3");
            fileState.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            fileState.LocalContentHash = "remote-hash";
            fileState.LocalSizeBytes = 12;
            fileState.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(fileState);
            RecordingSyncPairWork inner = new();
            RecordingRunProgressPublisher progressPublisher = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                new FakeCloudFilesAdapter(),
                readDiskState: path => path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase)
                    ? CreateNeutralDirectoryDiskState()
                    : CreateNeutralHydratedDiskState(),
                runProgressPublisher: progressPublisher);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Music", "Music/track.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(fileState.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(progressPublisher.Progress, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_ForwardsNeutralDirectorySubtreeWhenTrackedFileChanged()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            SyncStateEntry fileState = CreatePlaceholderState(syncPair, "Music/track.mp3");
            fileState.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            fileState.LocalContentHash = "remote-hash";
            fileState.LocalSizeBytes = 12;
            fileState.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(fileState);
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                new FakeCloudFilesAdapter(),
                readDiskState: path => path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase)
                    ? CreateNeutralDirectoryDiskState()
                    : new WindowsVirtualFileDiskState(
                        FileAttributes.Archive | FileAttributes.ReparsePoint,
                        Length: 13,
                        LastWriteUtc: new DateTime(2026, 06, 16, 10, 07, 00, DateTimeKind.Utc)));

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Music", "Music/track.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Music", "Music/track.mp3" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PassesPathToInnerSyncWhenContentHashDiffers()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter
            {
                ContentMatchesForDehydration = false,
            };
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            RecordingRunProgressPublisher progressPublisher = new();
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("edited-hash"),
                readDiskState: _ => CreateUnpinnedHydratedDiskState(),
                runProgressPublisher: progressPublisher);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.False);
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(progressPublisher.Progress, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesEditedPathsOnMergedFullRequest()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.xlsx"));
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                new FakeCloudFilesAdapter
                {
                    ContentMatchesForDehydration = false,
                },
                new FakeContentHasher("edited-hash"),
                readDiskState: _ => CreateUnpinnedHydratedDiskState());
            SyncRunRequest request = SyncRunRequest
                .ForFull(SyncRunCause.RealtimeRemoteChange)
                .Merge(SyncRunRequest.ForLocalChangedPaths(["Docs/report.xlsx"]));

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.True);
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.xlsx" }));
                Assert.That(
                    inner.Requests[0].Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.RealtimeRemoteChange));
            });
        }

        [Test]
        public async Task RunOnceAsync_RemovesHandledPathsAndSyncsRemainingPaths()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path => path.EndsWith("report.txt", StringComparison.OrdinalIgnoreCase)
                    ? CreateUnpinnedHydratedDiskState()
                    : null);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(
                    ["Docs/report.txt", "Docs/edited.txt"],
                    SyncRunCause.RealtimeRemoteChange));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.DehydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/edited.txt" }));
                Assert.That(inner.Requests[0].Causes, Is.EqualTo(SyncRunCause.RealtimeRemoteChange));
            });
        }

        [Test]
        public async Task RunOnceAsync_RemovesHandledPathsAndPreservesRemainingDeletedPaths()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                new FakeCloudFilesAdapter(),
                new FakeContentHasher("remote-hash"),
                readDiskState: path => path.EndsWith("report.txt", StringComparison.OrdinalIgnoreCase)
                    ? CreateUnpinnedHydratedDiskState()
                    : null);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(
                    ["Docs/report.txt", "Docs/deleted.txt"],
                    ["Docs/deleted.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
                Assert.That(inner.Requests[0].LocalDeletedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PassesFullRequestsThrough()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                new FakeSyncStateStore(),
                new FakeCloudFilesAdapter());

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_HydratesPinnedPathPreservedOnMergedFullRequest()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            int diskReads = 0;
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: _ => diskReads++ == 0
                    ? CreatePinnedRemoteOnlyDiskState()
                    : CreatePinnedHydratedDiskState());
            SyncRunRequest request = SyncRunRequest
                .ForLocalChangedPaths(["Docs/report.txt"])
                .Merge(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.True);
                Assert.That(
                    inner.Requests[0].Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.Periodic));
            });
        }
    }
}
