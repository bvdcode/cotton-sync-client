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
        public async Task RunOnceAsync_HydratesPinnedRootSubtreeAndSuppressesChildEvents()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Docs"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/track-one.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Docs/report.txt"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            Dictionary<string, int> fileReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string rootPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
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
        public async Task RunOnceAsync_PinnedRootHydrationForwardsAlreadyHydratedChildChange()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            SyncStateEntry fileState = CreatePlaceholderState(syncPair, "Music/track.mp3");
            fileState.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            fileState.LocalContentHash = "remote-hash";
            fileState.LocalSizeBytes = 12;
            fileState.LocalLastWriteUtc = new DateTime(2026, 06, 16, 10, 06, 00, DateTimeKind.Utc);
            stateStore.UpsertEntry(fileState);
            FakeCloudFilesAdapter cloudFiles = new()
            {
                PlaceholderState = WindowsCloudFilesPlaceholderState.Placeholder,
            };
            RecordingSyncPairWork inner = new();
            string rootPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("remote-hash"),
                readDiskState: path => string.Equals(
                        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        rootPath,
                        StringComparison.OrdinalIgnoreCase)
                    ? CreatePinnedDirectoryDiskState()
                    : CreatePinnedHydratedDiskState());

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths([".", "Music/track.mp3"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Music/track.mp3" }));
                Assert.That(cloudFiles.HydratedPaths, Is.Empty);
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
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/tracked.mp3"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            int fileReads = 0;
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
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
    }
}
