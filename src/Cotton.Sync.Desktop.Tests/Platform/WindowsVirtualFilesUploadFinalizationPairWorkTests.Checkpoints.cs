// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_PendingConversionDoesNotStarveLaterRemoteChangesOrDisappearAfterRestart()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activities = new();
            PublishingSyncPairWork core = new(activities, "Docs/video.mp4")
            {
                AdditionalUploadedPaths = ["Docs/new-local.txt"],
            };
            FakeSyncStateStore state = new();
            await AddPendingConversionAsync(state, pair, "Docs/video.mp4");
            await AddPendingConversionAsync(state, pair, "Docs/new-local.txt");
            await state.SaveChangeCursorAsync(new SyncChangeCursor
            {
                SyncPairId = pair.Id.ToString("D"),
                HasCompletedFullReconcile = true,
            });
            RecordingCloudFilesAdapter cloudFiles = new();
            cloudFiles.FileFailures.Add("Docs/video.mp4", new WindowsCloudFilesNativeException(
                "CfConvertToPlaceholder", unchecked((int)0x80070299)));
            FinalizationChangeFeed feed = new(pair);
            RemoteChangeAwareSyncPairWork CreateWork() => new(
                new WindowsVirtualFilesUploadFinalizationPairWork(core, activities, state, cloudFiles), feed, state);
            RemoteChangeAwareSyncPairWork work = CreateWork();

            Assert.ThrowsAsync<SyncFileMetadataPendingException>(
                async () => await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic)));
            Assert.That(feed.Cursor, Is.EqualTo(1000));
            Assert.That(core.Requests[0].IsFull, Is.False);
            Assert.That(cloudFiles.InSyncPaths, Does.Contain("Docs/new-local.txt"));

            work = CreateWork();
            Assert.ThrowsAsync<SyncFileMetadataPendingException>(
                async () => await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic)));
            Assert.That(feed.Cursor, Is.EqualTo(1001));
            Assert.That(core.Requests[1].LocalChangedPaths, Does.Contain("new-remote-1001.txt"));
            Assert.That(core.Requests[1].LocalChangedPaths, Does.Contain("Docs/video.mp4"));

            Assert.ThrowsAsync<SyncFileMetadataPendingException>(
                async () => await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic)));
            Assert.That(core.Requests[2].LocalChangedPaths, Is.EqualTo(new[] { "Docs/video.mp4" }));
            Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
            Assert.That((await state.GetAsync(pair.Id.ToString("D"), "Docs/video.mp4"))!.NeedsPlaceholderConversion, Is.True);

            cloudFiles.FileFailures.Clear();
            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));
            Assert.That((await state.GetAsync(pair.Id.ToString("D"), "Docs/video.mp4"))!.NeedsPlaceholderConversion, Is.False);
            Assert.That(cloudFiles.SyncRootInSyncPairs, Has.Count.EqualTo(1));
            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.Periodic));
            Assert.That(core.Requests, Has.Count.EqualTo(4), "A finished metadata conversion must stop being retried.");
        }

        [Test]
        public async Task RunOnceAsync_CoreFailureDoesNotAcknowledgeRemoteChangesEvenWithPendingMetadata()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activities = new();
            PublishingSyncPairWork core = new(activities, "Docs/video.mp4")
            {
                Failure = new SyncActionRequiredException("Remote deletion requires approval."),
            };
            FakeSyncStateStore state = new();
            await AddPendingConversionAsync(state, pair, "Docs/video.mp4");
            RecordingCloudFilesAdapter cloudFiles = new();
            cloudFiles.FileFailures.Add("Docs/video.mp4", new WindowsCloudFilesNativeException(
                "CfConvertToPlaceholder", unchecked((int)0x80070299)));
            FinalizationChangeFeed feed = new(pair);
            RemoteChangeAwareSyncPairWork work = new(
                new WindowsVirtualFilesUploadFinalizationPairWork(core, activities, state, cloudFiles), feed, state);

            SyncActionRequiredException? failure = Assert.ThrowsAsync<SyncActionRequiredException>(
                async () => await work.RunOnceAsync(pair));

            Assert.That(failure!.Message, Does.StartWith(core.Failure.Message));
            Assert.That(feed.Cursor, Is.Zero);
            Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
            Assert.That((await state.GetChangeCursorAsync(pair.Id.ToString("D"))).HasCompletedFullReconcile, Is.False);
        }

        private static async Task AddPendingConversionAsync(FakeSyncStateStore store, SyncPairSettings pair, string path)
        {
            store.UpsertFile(pair, path);
            SyncStateEntry entry = (await store.GetAsync(pair.Id.ToString("D"), path))!;
            entry.LocalContentHash = entry.RemoteContentHash;
            entry.LocalSizeBytes = entry.RemoteSizeBytes;
            entry.LocalLastWriteUtc = entry.SyncedAtUtc;
        }
    }
}
