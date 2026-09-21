// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_RecoveryDoesNotCompleteDirectoryWithAFailedChild()
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(pair, "Photos"));
            stateStore.UpsertEntry(CreatePlaceholderState(pair, "Photos/one.jpg"));
            stateStore.UpsertEntry(CreatePlaceholderState(pair, "Photos/two.jpg"));
            FakeCloudFilesAdapter cloudFiles = new();
            cloudFiles.Hydrating = path =>
            {
                if (path == "Photos/two.jpg")
                {
                    throw new IOException("Download failed.");
                }
            };
            WindowsVirtualFilesDehydrationPairWork work = new(new RecordingSyncPairWork(), stateStore, cloudFiles,
                new FakeContentHasher("remote-hash"), readDiskState: _ => CreatePinnedHydratedDiskState());

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.HydratedPaths, Is.EqualTo(new[] { "Photos/one.jpg" }));
                Assert.That(cloudFiles.PendingPaths, Is.EqualTo(new[] { "Photos" }));
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_PinnedSubtreeStaysPendingUntilEveryFileIsReady(bool fails)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertEntry(CreateDirectoryState(pair, "Pictures"));
            stateStore.UpsertEntry(CreateDirectoryState(pair, "Pictures/Album"));
            stateStore.UpsertEntry(CreateDirectoryState(pair, "Pictures/Album/Nested"));
            stateStore.UpsertEntry(CreatePlaceholderState(pair, "Pictures/Album/one.jpg"));
            stateStore.UpsertEntry(CreatePlaceholderState(pair, "Pictures/Album/Nested/two.jpg"));
            FakeCloudFilesAdapter cloudFiles = new();
            cloudFiles.Hydrating = _ =>
            {
                Assert.That(cloudFiles.PendingPaths, Is.EquivalentTo(new[] { "Pictures/Album", "Pictures/Album/Nested" }));
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
                if (fails)
                {
                    throw new IOException("Download failed.");
                }
            };
            WindowsVirtualFilesDehydrationPairWork work = new(
                new RecordingSyncPairWork(), stateStore, cloudFiles, new FakeContentHasher("remote-hash"),
                readDiskState: path => Path.HasExtension(path)
                    ? CreatePinnedHydratedDiskState()
                    : CreatePinnedDirectoryDiskState());

            Task run = work.RunOnceAsync(pair, SyncRunRequest.ForLocalChangedPaths(["Pictures/Album"]));
            if (fails)
            {
                Assert.ThrowsAsync<IOException>(async () => await run);
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
            }
            else
            {
                await run;
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Pictures/Album/Nested", "Pictures/Album" }));
            }
            Assert.That(cloudFiles.PendingPaths, Does.Not.Contain("Pictures"));
        }
    }
}
