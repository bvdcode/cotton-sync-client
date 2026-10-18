// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_FileSystemLimitationFinalizesOtherFilesAndKeepsFailedStateForRetry()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/a.mp4")
            {
                AdditionalUploadedPaths = ["Docs/b.txt", "Docs/c.mp4"],
            };
            FakeSyncStateStore stateStore = new();
            foreach (string path in new[] { "Docs/a.mp4", "Docs/b.txt", "Docs/c.mp4" })
            {
                stateStore.UpsertFile(syncPair, path);
            }

            RecordingCloudFilesAdapter cloudFiles = new();
            cloudFiles.FileFailures.Add("Docs/a.mp4", new WindowsCloudFilesNativeException("CfConvertToPlaceholder", unchecked((int)0x80070299)));
            cloudFiles.FileFailures.Add("Docs/c.mp4", new WindowsCloudFilesNativeException("CfConvertToPlaceholder", unchecked((int)0x80070299)));
            RecordingRunProgressPublisher progress = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(
                inner, activityPublisher, stateStore, cloudFiles, runProgressPublisher: progress);

            SyncActionRequiredException? failure = Assert.ThrowsAsync<SyncActionRequiredException>(
                async () => await work.RunOnceAsync(syncPair, SyncRunRequest.Full));
            SyncStateEntry? failedState = await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/a.mp4");
            SyncStateEntry? completedState = await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/b.txt");
            Assert.Multiple(() =>
            {
                Assert.That(failure!.Message, Does.Contain("Docs/a.mp4").And.Contain("Docs/c.mp4").And.Contain("0x80070299"));
                Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Docs/b.txt" }));
                Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
                Assert.That(cloudFiles.DirectoryPlaceholders, Is.Empty);
                Assert.That(failedState!.PlaceholderIdentity, Is.Null);
                Assert.That(completedState!.PlaceholderIdentity, Is.Not.Empty);
                Assert.That(progress.Progress.Last().IsCompleted, Is.True);
                Assert.That(progress.Progress.Last().FilesCompleted, Is.EqualTo(1));
            });

            cloudFiles.FileFailures.Clear();
            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);
            failedState = await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/a.mp4");
            Assert.That(failedState!.PlaceholderIdentity, Is.Not.Empty);
            Assert.That(cloudFiles.SyncRootInSyncPairs, Has.Count.EqualTo(1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RunOnceAsync_PartialSyncFailureFinalizesCompletedTransfersWithoutAcknowledgingSuccess(bool hasFinalizationFailure)
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            SyncActionRequiredException originalFailure = new("Another item requires review.");
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/report.txt") { Failure = originalFailure };
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            RecordingCloudFilesAdapter cloudFiles = new();
            if (hasFinalizationFailure)
            {
                cloudFiles.FileFailures.Add("Docs/report.txt", new WindowsCloudFilesNativeException("CfConvertToPlaceholder", unchecked((int)0x80070299)));
            }

            WindowsVirtualFilesUploadFinalizationPairWork work = new(inner, activityPublisher, stateStore, cloudFiles);

            SyncActionRequiredException? failure = Assert.ThrowsAsync<SyncActionRequiredException>(
                async () => await work.RunOnceAsync(syncPair, SyncRunRequest.Full));
            SyncStateEntry? state = await stateStore.GetAsync(syncPair.Id.ToString("D"), "Docs/report.txt");
            Assert.That(failure!.Message, Does.Contain(originalFailure.Message));
            Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
            if (hasFinalizationFailure)
            {
                Assert.That(failure.Message, Does.Contain("Docs/report.txt"));
                Assert.That(state!.PlaceholderIdentity, Is.Null);
            }
            else
            {
                Assert.That(failure, Is.SameAs(originalFailure));
                Assert.That(state!.PlaceholderIdentity, Is.Not.Empty);
            }
        }

        [Test]
        public void RunOnceAsync_CancellationDoesNotFinalizeCompletedTransfers()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            OperationCanceledException cancellation = new();
            PublishingSyncPairWork inner = new(activityPublisher, "Docs/report.txt") { Failure = cancellation };
            RecordingCloudFilesAdapter cloudFiles = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(inner, activityPublisher, new FakeSyncStateStore(), cloudFiles);

            Assert.That(Assert.ThrowsAsync<OperationCanceledException>(
                async () => await work.RunOnceAsync(syncPair, SyncRunRequest.Full)), Is.SameAs(cancellation));
            Assert.That(cloudFiles.InSyncPaths, Is.Empty);
            Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
        }
    }
}
