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
        public async Task RunOnceAsync_HydratesOnlyPinnedDirectorySubtreeAndSuppressesChildEvents()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music/Album"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-one.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/Album/track-two.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Other/outside.mp3"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            Dictionary<string, int> fileReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                path =>
                {
                    if (path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase))
                    {
                        return CreatePinnedDirectoryDiskState();
                    }

                    fileReads.TryGetValue(path, out int readCount);
                    fileReads[path] = readCount + 1;
                    return readCount == 0 ? CreatePinnedRemoteOnlyDiskState() : CreatePinnedHydratedDiskState();
                },
                suppression);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(
                    ["Music", "Music/track-one.mp3", "Music/Album/track-two.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(
                    cloudFiles.HydratedPaths,
                    Is.EquivalentTo(new[] { "Music/Album/track-two.mp3", "Music/track-one.mp3" }));
                Assert.That(cloudFiles.PinnedPaths, Is.EqualTo(new[] { "Music/Album" }));
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Music/Album", "Music" }));
                Assert.That(cloudFiles.HydratedPaths, Does.Not.Contain("Other/outside.mp3"));
                Assert.That(
                    stateStore.GetRequired(syncPair.Id, "Music/track-one.mp3").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(
                    stateStore.GetRequired(syncPair.Id, "Music/Album/track-two.mp3").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(
                    diagnostics.Snapshot().Last().Operation,
                    Is.EqualTo("manual-always-keep-directory"));
                Assert.That(suppression.SuppressedPinnedWrites, Has.Count.EqualTo(3));
                Assert.That(suppression.ProviderWriteBurstCount, Is.EqualTo(1));
                Assert.That(stateStore.UpsertManyCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_PinnedDirectoryRestoresMissingTrackedPlaceholderBeforeHydration()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/missing.mp3"));
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingSyncPairWork inner = new();
            RecordingLocalChangeSuppression suppression = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                path => path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase)
                    ? CreatePinnedDirectoryDiskState()
                    : cloudFiles.RestoredPaths.Count == 0
                        ? null
                        : CreatePinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Music", "Music/missing.mp3"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Music/missing.mp3");
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.RestoredPaths, Is.EqualTo(new[] { "Music/missing.mp3" }));
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Music/missing.mp3" }));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(
                    diagnostics.Snapshot().Select(static item => (item.Operation, item.Status)),
                    Does.Contain(("manual-always-keep-placeholder-repair", "completed")));
                Assert.That(diagnostics.Snapshot().Any(static item => item.Status == "failed"), Is.False);
                Assert.That(suppression.SuppressedPinnedWrites, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_AlreadyHydratedPinnedDirectoryDoesNotSuppressLaterUserChanges()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            SyncStateEntry fileState = CreatePlaceholderState(syncPair, "Music/track.mp3");
            fileState.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            stateStore.UpsertEntry(fileState);
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            RecordingLocalChangeSuppression suppression = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path => path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase)
                    ? CreatePinnedDirectoryDiskState()
                    : CreatePinnedHydratedDiskState(),
                localChangeSuppression: suppression);

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Music", "Music/track.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Music/track.mp3" }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(suppression.SuppressedWrites, Is.Empty);
                Assert.That(suppression.SuppressedPinnedWrites, Is.Empty);
                Assert.That(suppression.ProviderWriteBurstCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_HydratesPinnedDirectorySubtreePublishesAggregateProgress()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-one.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-two.mp3"));
            RecordingRunProgressPublisher progressPublisher = new();
            Dictionary<string, int> fileReads = new(StringComparer.OrdinalIgnoreCase);
            WindowsVirtualFilesDehydrationPairWork work = new(
                new RecordingSyncPairWork(),
                stateStore,
                new FakeCloudFilesAdapter(),
                new FakeContentHasher("remote-hash"),
                readDiskState: path =>
                {
                    if (path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase))
                    {
                        return CreatePinnedDirectoryDiskState();
                    }

                    fileReads.TryGetValue(path, out int readCount);
                    fileReads[path] = readCount + 1;
                    return readCount == 0 ? CreatePinnedRemoteOnlyDiskState() : CreatePinnedHydratedDiskState();
                },
                runProgressPublisher: progressPublisher);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(
                ["Music/track-one.mp3", "Music/track-two.mp3", "Music"],
                SyncRunCause.LocalChange);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(
                    progressPublisher.Progress.Select(static progress => new
                    {
                        progress.Stage,
                        progress.FilesCompleted,
                        progress.FilesTotal,
                        progress.IsCompleted,
                    }),
                    Is.EqualTo(new[]
                    {
                        new { Stage = SyncRunProgressStage.HydratingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.HydratingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.HydratingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.HydratingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.HydratingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.HydratingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)2, IsCompleted = true },
                    }));
                Assert.That(progressPublisher.Progress.Select(static progress => progress.Causes), Is.All.EqualTo(SyncRunCause.LocalChange));
                Assert.That(progressPublisher.Progress.Select(static progress => progress.RequestedPathCount), Is.All.EqualTo(3));
            });
        }

        [Test]
        public async Task RunOnceAsync_DehydratesTrackedFilesPublishesAggregateProgress()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-one.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-two.mp3"));
            RecordingRunProgressPublisher progressPublisher = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                new RecordingSyncPairWork(),
                stateStore,
                new FakeCloudFilesAdapter(),
                new FakeContentHasher("remote-hash"),
                readDiskState: _ => CreateUnpinnedHydratedDiskState(),
                runProgressPublisher: progressPublisher);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(
                ["Music/track-one.mp3", "Music/track-two.mp3"],
                SyncRunCause.LocalChange);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(
                    progressPublisher.Progress.Select(static progress => new
                    {
                        progress.Stage,
                        progress.FilesCompleted,
                        progress.FilesTotal,
                        progress.CurrentPath,
                        progress.IsCompleted,
                    }),
                    Is.EqualTo(new[]
                    {
                        new { Stage = SyncRunProgressStage.DehydratingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)2, CurrentPath = string.Empty, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.DehydratingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)2, CurrentPath = "Music/track-one.mp3", IsCompleted = false },
                        new { Stage = SyncRunProgressStage.DehydratingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)2, CurrentPath = "Music/track-one.mp3", IsCompleted = false },
                        new { Stage = SyncRunProgressStage.DehydratingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)2, CurrentPath = "Music/track-two.mp3", IsCompleted = false },
                        new { Stage = SyncRunProgressStage.DehydratingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)2, CurrentPath = "Music/track-two.mp3", IsCompleted = false },
                        new { Stage = SyncRunProgressStage.DehydratingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)2, CurrentPath = string.Empty, IsCompleted = true },
                    }));
                Assert.That(
                    progressPublisher.Progress.Select(static progress => progress.Causes),
                    Is.All.EqualTo(SyncRunCause.LocalChange));
                Assert.That(
                    progressPublisher.Progress.Select(static progress => progress.RequestedPathCount),
                    Is.All.EqualTo(2));
            });
        }
    }
}
