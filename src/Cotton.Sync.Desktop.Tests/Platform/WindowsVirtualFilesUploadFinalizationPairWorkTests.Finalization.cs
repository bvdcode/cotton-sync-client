// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadedActivityMarksCloudFilesPathInSync()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new InMemoryAppActivityPublisher();
            PublishingSyncPairWork inner = new PublishingSyncPairWork(activityPublisher, "Docs/Reports/report.txt");
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertFile(syncPair, "Docs/Reports/report.txt");
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            stateStore.UpsertDirectory(syncPair, "Docs/Reports", Guid.Parse("44444444-4444-4444-4444-444444444444"));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            WindowsVirtualFilesUploadFinalizationPairWork work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles,
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            SyncStateEntry? finalizedState = await stateStore
                .GetAsync(syncPair.Id.ToString("D"), "Docs/Reports/report.txt");

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(
                    cloudFiles.InSyncPaths,
                    Is.EqualTo(new[] { "Docs/Reports/report.txt" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath),
                    Is.EqualTo(new[] { "Docs/Reports", "Docs" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RemoteDirectory.Id),
                    Is.EqualTo(new[]
                    {
                        Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    }));
                Assert.That(
                    cloudFiles.SyncRootInSyncPairs.Select(static item => item.Id),
                    Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(
                    suppression.MetadataSuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports/report.txt"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs"),
                    }));
                Assert.That(finalizedState, Is.Not.Null);
                Assert.That(finalizedState!.PlaceholderIdentity, Is.Not.Null.And.Not.Empty);
                Assert.That(finalizedState.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(finalizedState.LocalSizeBytes, Is.EqualTo(25));
                Assert.That(finalizedState.LocalLastWriteUtc, Is.EqualTo(
                    new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesConvergedActivityMarksCloudFilesPathInSync()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(
                activityPublisher,
                "Docs/report.txt",
                SyncActivityKind.Converged);
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            stateStore.UpsertDirectory(
                syncPair,
                "Docs",
                Guid.Parse("33333333-3333-3333-3333-333333333333"));
            RecordingCloudFilesAdapter cloudFiles = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.That(cloudFiles.InSyncPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesConflictActivityDoesNotFinalizePath()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new();
            PublishingSyncPairWork inner = new(
                activityPublisher,
                "Docs/report.txt",
                SyncActivityKind.Conflict);
            FakeSyncStateStore stateStore = new();
            stateStore.UpsertFile(syncPair, "Docs/report.txt");
            RecordingCloudFilesAdapter cloudFiles = new();
            WindowsVirtualFilesUploadFinalizationPairWork work = new(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles);

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            SyncStateEntry? finalizedState = await stateStore
                .GetAsync(syncPair.Id.ToString("D"), "Docs/report.txt");
            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
                Assert.That(cloudFiles.SyncRootInSyncPairs, Is.Empty);
                Assert.That(finalizedState, Is.Not.Null);
                Assert.That(finalizedState!.PlaceholderIdentity, Is.Null);
                Assert.That(
                    finalizedState.PlaceholderHydrationState,
                    Is.EqualTo(SyncPlaceholderHydrationState.None));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadedDirectoryActivityFinalizesDirectoryPlaceholder()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new InMemoryAppActivityPublisher();
            PublishingSyncPairWork inner = new PublishingSyncPairWork(activityPublisher, "Docs");
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            Guid remoteNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            stateStore.UpsertDirectory(syncPair, "Docs", remoteNodeId);
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            WindowsVirtualFilesUploadFinalizationPairWork work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                cloudFiles,
                suppression);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs"]));

            Assert.Multiple(() =>
            {
                Assert.That(cloudFiles.InSyncPaths, Is.Empty);
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath),
                    Is.EqualTo(new[] { "Docs" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RemoteDirectory.Id),
                    Is.EqualTo(new[] { remoteNodeId }));
                Assert.That(
                    cloudFiles.SyncRootInSyncPairs.Select(static item => item.Id),
                    Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(
                    suppression.MetadataSuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs"),
                    }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesUploadedActivityPublishesFinalizingProgress()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            InMemoryAppActivityPublisher activityPublisher = new InMemoryAppActivityPublisher();
            PublishingSyncPairWork inner = new PublishingSyncPairWork(activityPublisher, "Docs/Reports/report.txt");
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            stateStore.UpsertFile(syncPair, "Docs/Reports/report.txt");
            stateStore.UpsertDirectory(syncPair, "Docs", Guid.Parse("33333333-3333-3333-3333-333333333333"));
            stateStore.UpsertDirectory(syncPair, "Docs/Reports", Guid.Parse("44444444-4444-4444-4444-444444444444"));
            RecordingRunProgressPublisher progressPublisher = new RecordingRunProgressPublisher();
            WindowsVirtualFilesUploadFinalizationPairWork work = new WindowsVirtualFilesUploadFinalizationPairWork(
                inner,
                activityPublisher,
                stateStore,
                new RecordingCloudFilesAdapter(),
                runProgressPublisher: progressPublisher);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

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
                    new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)4, IsCompleted = false },
                    new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)4, IsCompleted = false },
                    new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)4, IsCompleted = false },
                    new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 3, FilesTotal = (int?)4, IsCompleted = false },
                    new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 4, FilesTotal = (int?)4, IsCompleted = true },
                }));
        }

    }
}
