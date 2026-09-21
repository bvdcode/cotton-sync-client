// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [TestCase(false, SyncPlaceholderHydrationState.RemoteOnly)]
        [TestCase(false, SyncPlaceholderHydrationState.Hydrated)]
        [TestCase(true, SyncPlaceholderHydrationState.RemoteOnly)]
        [TestCase(true, SyncPlaceholderHydrationState.Hydrated)]
        public async Task RunOnceAsync_FinalizesPinnedRegularFileThatMatchesRemote(
            bool startupRecovery, SyncPlaceholderHydrationState hydrationState)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(pair, "Pictures"));
            SyncStateEntry state = CreatePlaceholderState(pair, "Pictures/photo.jpg");
            state.RemoteSizeBytes = CreatePinnedRegularFileDiskState().Length;
            state.PlaceholderHydrationState = hydrationState;
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner, stateStore, cloudFiles, new FakeContentHasher("remote-hash"),
                readDiskState: path => path.EndsWith("Pictures", StringComparison.OrdinalIgnoreCase)
                    ? CreatePinnedDirectoryDiskState()
                    : CreatePinnedRegularFileDiskState());
            SyncRunRequest request = startupRecovery
                ? SyncRunRequest.ForFull(SyncRunCause.Periodic)
                : SyncRunRequest.ForLocalChangedPaths(["Pictures"]);

            await work.RunOnceAsync(pair, request);

            SyncStateEntry updated = stateStore.GetRequired(pair.Id, state.RelativePath);
            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.FinalizedPaths, Is.EqualTo(new[] { "Pictures/photo.jpg" }));
                Assert.That(updated.LocalContentHash, Is.EqualTo(updated.RemoteContentHash));
                Assert.That(updated.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(stateStore.UpsertManyCallCount, Is.EqualTo(1));
                Assert.That(inner.Requests.Count, Is.EqualTo(startupRecovery ? 1 : 0));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_DoesNotFinalizePinnedRegularFileWithLocalEdits(bool startupRecovery)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(pair, "Pictures"));
            SyncStateEntry state = CreatePlaceholderState(pair, "Pictures/photo.jpg");
            state.RemoteSizeBytes = CreatePinnedRegularFileDiskState().Length;
            stateStore.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                new RecordingSyncPairWork(), stateStore, cloudFiles, new FakeContentHasher("edited-local-hash"),
                readDiskState: path => path.EndsWith("Pictures", StringComparison.OrdinalIgnoreCase)
                    ? CreatePinnedDirectoryDiskState()
                    : CreatePinnedRegularFileDiskState());
            if (startupRecovery)
            {
                await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));
            }
            else
            {
                Assert.ThrowsAsync<InvalidOperationException>(() => work.RunOnceAsync(
                    pair, SyncRunRequest.ForLocalChangedPaths(["Pictures"])));
            }

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.FinalizedPaths, Is.Empty);
                Assert.That(stateStore.UpsertManyCallCount, Is.Zero);
                Assert.That(state.LocalContentHash, Is.Null);
                Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }
    }
}
