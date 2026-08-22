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
        public async Task RunOnceAsync_PeriodicRecoveryHydratesPersistedPinnedOfflineFilesOnce()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreateDirectoryState(syncPair, "Music"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/pinned.mp3"));
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/online-only.mp3"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            Dictionary<string, int> diskReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
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
        public async Task RunOnceAsync_StalePinnedRecoveryYieldsToPrimarySync()
        {
            SyncPairSettings syncPair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreatePlaceholderState(syncPair, "Music/stale.mp3"));
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            int diskReads = 0;
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner,
                stateStore,
                cloudFiles,
                new FakeContentHasher("stale-local-hash"),
                diagnostics,
                _ => diskReads++ == 0
                    ? CreatePinnedRemoteOnlyDiskState()
                    : CreatePinnedHydratedDiskState());
            SyncRunRequest request = SyncRunRequest.ForFull(SyncRunCause.Periodic);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.True);
                Assert.That(inner.Requests[0].Causes, Is.EqualTo(request.Causes));
                Assert.That(
                    diagnostics.Snapshot().Any(item =>
                        item.Operation == "manual-always-keep-recovery"
                        && item.Status == "skipped"
                        && item.RelativePath == "Music/stale.mp3"),
                    Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_PeriodicRecoveryRunsOnceForEachVirtualFilesPair()
        {
            SyncPairSettings firstPair = CreateVirtualFilesPair();
            SyncPairSettings secondPair = CreateVirtualFilesPair();
            secondPair.Id = Guid.Parse("55555555-5555-5555-5555-555555555555");
            secondPair.LocalRootPath = Path.Combine(Path.GetTempPath(), "cotton-vfs-second-root");
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertEntry(CreatePlaceholderState(firstPair, "pinned.txt"));
            stateStore.UpsertEntry(CreatePlaceholderState(secondPair, "pinned.txt"));
            FakeCloudFilesAdapter cloudFiles = new FakeCloudFilesAdapter();
            Dictionary<string, int> diskReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            WindowsVirtualFilesDehydrationPairWork work = new WindowsVirtualFilesDehydrationPairWork(
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
    }
}
