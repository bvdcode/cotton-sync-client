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
    public class WindowsVirtualFilesDehydrationPairWorkTests
    {
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        [Test]
        public async Task RunOnceAsync_HydratesPinnedRemoteOnlyPlaceholderAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            stateStore.UpsertEntry(state);
            var cloudFiles = new FakeCloudFilesAdapter();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var inner = new RecordingSyncPairWork();
            var suppression = new RecordingLocalChangeSuppression();
            int diskReads = 0;
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => diskReads++ == 0 ? CreatePinnedRemoteOnlyDiskState() : CreatePinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(
                    suppression.SuppressedWrites,
                    Is.EqualTo(new[] { new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/report.txt") }));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-always-keep"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithAlreadyHydratedPinnedPlaceholderSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = "remote-hash";
            state.LocalSizeBytes = 12;
            state.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(state);
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
                _ => CreatePinnedHydratedDiskState(),
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(suppression.SuppressedWrites, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
                Assert.That(diagnostic.Operation, Is.EqualTo("manual-always-keep"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.Details, Does.Contain("already hydrated"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PinnedRegularFileEditRunsInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = "remote-hash";
            state.LocalSizeBytes = 12;
            state.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("edited-local-hash"),
                readDiskState: _ => CreatePinnedRegularFileDiskState());
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.EqualTo(new[] { request }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_RecordsCompletedOnDemandHydrationAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                _ => CreateMaterializedDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(updated.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(updated.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(updated.LocalLastWriteUtc, Is.EqualTo(new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc)));
                Assert.That(diagnostic.Operation, Is.EqualTo("on-demand-hydration"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PassesMaterializedPathToInnerSyncWhenContentDiffers()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("edited-hash"),
                diagnostics,
                _ => CreateMaterializedDiskState());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry updated = stateStore.GetRequired(syncPair.Id, "Docs/report.txt");
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(cloudFiles.DehydratedPaths, Is.Empty);
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(updated.LocalContentHash, Is.Null);
                Assert.That(diagnostics.Snapshot(), Is.Empty);
            });
        }

        [Test]
        public async Task RunOnceAsync_HydratesOnlyPinnedDirectorySubtreeAndSuppressesChildEvents()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music/Album"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-one.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/Album/track-two.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Other/outside.mp3"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var inner = new RecordingSyncPairWork();
            var suppression = new RecordingLocalChangeSuppression();
            var fileReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var work = new WindowsVirtualFilesDehydrationPairWork(
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
                Assert.That(suppression.SuppressedWrites, Has.Count.EqualTo(3));
                Assert.That(suppression.ProviderWriteBurstCount, Is.EqualTo(1));
                Assert.That(stateStore.UpsertManyCallCount, Is.EqualTo(1));
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
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(suppression.SuppressedWrites, Is.Empty);
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

        [Test]
        public async Task RunOnceAsync_HydratesPinnedRootSubtreeAndSuppressesChildEvents()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Docs"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-one.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var inner = new RecordingSyncPairWork();
            var suppression = new RecordingLocalChangeSuppression();
            var fileReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string rootPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                diagnostics,
                path =>
                {
                    string normalizedPath = Path.GetFullPath(path)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase))
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
                SyncRunRequest.ForLocalChangedPaths([".", "Music/track-one.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(
                    cloudFiles.HydratedPaths,
                    Is.EquivalentTo(new[] { "Music/track-one.mp3", "Docs/report.txt" }));
                Assert.That(cloudFiles.InSyncPaths, Is.EquivalentTo(new[] { "Music", "Docs" }));
                Assert.That(
                    stateStore.GetRequired(syncPair.Id, "Music/track-one.mp3").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(
                    stateStore.GetRequired(syncPair.Id, "Docs/report.txt").PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(
                    diagnostics.Snapshot().Select(static item => item.Operation),
                    Does.Contain("manual-always-keep-root"));
                Assert.That(suppression.ProviderWriteBurstCount, Is.EqualTo(1));
                Assert.That(stateStore.UpsertManyCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_ForwardsRootScopedRequestAsFullWhenRootHydrationIsNotHandled()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            string rootPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                new FakeSyncStateStore(),
                cloudFiles,
                readDiskState: path =>
                {
                    string normalizedPath = Path.GetFullPath(path)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase)
                        ? CreateUnpinnedDirectoryDiskState()
                        : null;
                });

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["."], SyncRunCause.LocalChange));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.True);
                Assert.That(inner.Requests[0].Causes, Is.EqualTo(SyncRunCause.LocalChange));
            });
        }

        [Test]
        public async Task RunOnceAsync_IgnoresRedundantUnpinnedRootEventWhileHandlingPinnedDirectory()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track.mp3"));
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            string rootPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path =>
                {
                    string normalizedPath = Path.GetFullPath(path)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return CreateUnpinnedDirectoryDiskState();
                    }

                    if (path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase))
                    {
                        return CreatePinnedDirectoryDiskState();
                    }

                    return CreatePinnedHydratedDiskState();
                });

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths([".", "Music", "Music/track.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Is.Empty);
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Music" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PreservesUntrackedChildEventWhileHydratingPinnedDirectory()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/tracked.mp3"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            int fileReads = 0;
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path => path.EndsWith(Path.DirectorySeparatorChar + "Music", StringComparison.OrdinalIgnoreCase)
                    ? CreatePinnedDirectoryDiskState()
                    : fileReads++ == 0
                        ? CreatePinnedRemoteOnlyDiskState()
                        : CreatePinnedHydratedDiskState());

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(["Music", "Music/new-local.txt", "Music/tracked.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Music/tracked.mp3" }));
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.False);
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Music/new-local.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_DehydratesSafeUnpinnedPlaceholderAndSuppressesInnerSync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            SyncStateEntry state = CreatePlaceholderState(syncPair, "Docs/report.txt");
            stateStore.UpsertEntry(state);
            var cloudFiles = new FakeCloudFilesAdapter();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var inner = new RecordingSyncPairWork();
            var suppression = new RecordingLocalChangeSuppression();
            var work = new WindowsVirtualFilesDehydrationPairWork(
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
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            RecordingRunProgressPublisher progressPublisher = new();
            var work = new WindowsVirtualFilesDehydrationPairWork(
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
        public async Task RunOnceAsync_RemovesHandledPathsAndSyncsRemainingPaths()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            var work = new WindowsVirtualFilesDehydrationPairWork(
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
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var inner = new RecordingSyncPairWork();
            var work = new WindowsVirtualFilesDehydrationPairWork(
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
            var inner = new RecordingSyncPairWork();
            var work = new WindowsVirtualFilesDehydrationPairWork(
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
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            int diskReads = 0;
            var work = new WindowsVirtualFilesDehydrationPairWork(
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

        [Test]
        public async Task RunOnceAsync_PeriodicRecoveryHydratesPersistedPinnedOfflineFilesOnce()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/pinned.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/online-only.mp3"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var inner = new RecordingSyncPairWork();
            var diskReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var work = new WindowsVirtualFilesDehydrationPairWork(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path =>
                {
                    if (path.EndsWith("online-only.mp3", StringComparison.OrdinalIgnoreCase))
                    {
                        return CreateUnpinnedRemoteOnlyDiskState();
                    }

                    diskReads.TryGetValue(path, out int readCount);
                    diskReads[path] = readCount + 1;
                    return readCount == 0
                        ? CreatePinnedRemoteOnlyDiskState()
                        : CreatePinnedHydratedDiskState();
                });
            SyncRunRequest recoveryRequest = SyncRunRequest.ForFull(SyncRunCause.Periodic);

            await work.RunOnceAsync(syncPair, recoveryRequest);
            await work.RunOnceAsync(syncPair, recoveryRequest);

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Music/pinned.mp3" }));
                Assert.That(cloudFiles.HydratedPaths, Does.Not.Contain("Music/online-only.mp3"));
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Music" }));
                Assert.That(inner.Requests, Has.Count.EqualTo(2));
                Assert.That(inner.Requests, Has.All.Matches<SyncRunRequest>(request => request.IsFull));
                Assert.That(stateStore.UpsertManyCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunOnceAsync_PeriodicRecoveryRunsOnceForEachVirtualFilesPair()
        {
            SyncPairSettings firstPair = CreateVirtualFilesPair();
            SyncPairSettings secondPair = CreateVirtualFilesPair();
            secondPair.Id = Guid.Parse("55555555-5555-5555-5555-555555555555");
            secondPair.LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-second-root");
            var stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(firstPair, "pinned.txt"));
            stateStore.UpsertEntry(CreatePlaceholderState(secondPair, "pinned.txt"));
            var cloudFiles = new FakeCloudFilesAdapter();
            var diskReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var work = new WindowsVirtualFilesDehydrationPairWork(
                new RecordingSyncPairWork(),
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path =>
                {
                    diskReads.TryGetValue(path, out int readCount);
                    diskReads[path] = readCount + 1;
                    return readCount == 0
                        ? CreatePinnedRemoteOnlyDiskState()
                        : CreatePinnedHydratedDiskState();
                });
            SyncRunRequest recoveryRequest = SyncRunRequest.ForFull(SyncRunCause.Periodic);

            await work.RunOnceAsync(firstPair, recoveryRequest);
            await work.RunOnceAsync(secondPair, recoveryRequest);

            Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "pinned.txt", "pinned.txt" }));
        }

        private static WindowsVirtualFileDiskState CreateUnpinnedHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributeUnpinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedRemoteOnlyDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | FileAttributes.Offline
                | (FileAttributes)FileAttributePinned
                | (FileAttributes)FileAttributeRecallOnDataAccess;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateUnpinnedRemoteOnlyDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive
                | FileAttributes.ReparsePoint
                | FileAttributes.Offline
                | (FileAttributes)FileAttributeUnpinned
                | (FileAttributes)FileAttributeRecallOnDataAccess;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedDirectoryDiskState()
        {
            FileAttributes attributes = FileAttributes.Directory
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 0,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateUnpinnedDirectoryDiskState()
        {
            FileAttributes attributes = FileAttributes.Directory
                | FileAttributes.ReparsePoint
                | (FileAttributes)FileAttributeUnpinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 0,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreatePinnedRegularFileDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive | (FileAttributes)FileAttributePinned;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 24,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateNeutralHydratedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive | FileAttributes.ReparsePoint;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateMaterializedDiskState()
        {
            FileAttributes attributes = FileAttributes.Archive | FileAttributes.ReparsePoint;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 12,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static WindowsVirtualFileDiskState CreateNeutralDirectoryDiskState()
        {
            FileAttributes attributes = FileAttributes.Directory | FileAttributes.ReparsePoint;
            return new WindowsVirtualFileDiskState(
                attributes,
                Length: 0,
                LastWriteUtc: new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc));
        }

        private static SyncPairSettings CreateVirtualFilesPair()
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Desktop",
                LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-root"),
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                RemoteDisplayPath = "/Desktop",
                IsEnabled = true,
                Mode = SyncPairMode.WindowsVirtualFiles,
            };
        }

        private static SyncStateEntry CreatePlaceholderState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.File,
                RemoteFileId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                RemoteNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                RemoteContentHash = "remote-hash",
                RemoteSizeBytes = 12,
                PlaceholderIdentity = [1, 2, 3],
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
            };
        }

        private static SyncStateEntry CreateDirectoryState(SyncPairSettings syncPair, string relativePath)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.Id.ToString("D"),
                RelativePath = relativePath,
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = Guid.NewGuid(),
                PlaceholderHydrationState = SyncPlaceholderHydrationState.RemoteOnly,
                SyncedAtUtc = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
            };
        }

        private sealed class RecordingSyncPairWork : ISyncPairWork
        {
            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                Requests.Add(SyncRunRequest.Full);
                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeContentHasher : ILocalFileContentHasher
        {
            private readonly string _hash;

            public FakeContentHasher(string hash)
            {
                _hash = hash;
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_hash);
            }
        }

        private sealed record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

        private sealed class RecordingLocalChangeSuppression : ILocalChangeSuppression
        {
            public List<SuppressedWrite> SuppressedWrites { get; } = [];

            public int ProviderWriteBurstCount { get; private set; }

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
            {
                ProviderWriteBurstCount++;
                return NoopDisposable.Instance;
            }

            public bool ShouldSuppress(LocalSyncRootChange change)
            {
                return false;
            }
        }

        private class RecordingRunProgressPublisher : IAppRunProgressPublisher
        {
            public List<AppRunProgress> Progress { get; } = [];

            public IDisposable Subscribe(IObserver<AppRunProgress> observer)
            {
                return NoopDisposable.Instance;
            }

            public void Publish(AppRunProgress progress)
            {
                Progress.Add(progress);
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<string> DehydratedPaths { get; } = [];

            public List<string> HydratedPaths { get; } = [];

            public List<string> PinnedPaths { get; } = [];

            public List<string> InSyncPaths { get; } = [];

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                DehydratedPaths.Add(relativePath);
            }

            public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                HydratedPaths.Add(relativePath);
            }

            public void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                PinnedPaths.Add(relativePath);
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                InSyncPaths.Add(relativePath);
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeSyncStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public int UpsertManyCallCount { get; private set; }

            public void UpsertEntry(SyncStateEntry entry)
            {
                _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
            }

            public SyncStateEntry GetRequired(Guid syncPairId, string relativePath)
            {
                return _entries[CreateKey(syncPairId.ToString("D"), relativePath)];
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<SyncStateEntry> entries = _entries.Values
                    .Where(entry => string.Equals(entry.SyncPairId, syncPairId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return Task.FromResult(entries);
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (SyncStateEntry entry in _entries.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(entry.SyncPairId, syncPairId, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return entry;
                    }
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.TryGetValue(CreateKey(syncPairId, relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                UpsertEntry(entry);
                return Task.CompletedTask;
            }

            public Task UpsertManyAsync(
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                UpsertManyCallCount++;
                foreach (SyncStateEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UpsertEntry(entry);
                }

                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                _entries.Remove(CreateKey(syncPairId, relativePath));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries
                    .Where(item => item.Value.SyncPairId.Equals(syncPairId, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Key)
                    .ToArray())
                {
                    _entries.Remove(key);
                }

                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries
                    .Where(item => item.Value.SyncPairId.Equals(syncPairId, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Key)
                    .ToArray())
                {
                    _entries.Remove(key);
                }

                foreach (SyncStateEntry entry in entries)
                {
                    UpsertEntry(entry);
                }

                return Task.CompletedTask;
            }

            private static string CreateKey(string syncPairId, string relativePath)
            {
                return syncPairId.ToUpperInvariant() + "|" + SyncPath.ToKey(relativePath);
            }
        }
    }
}
