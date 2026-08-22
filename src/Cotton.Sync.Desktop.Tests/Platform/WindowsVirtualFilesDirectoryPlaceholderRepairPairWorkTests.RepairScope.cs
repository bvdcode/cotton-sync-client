// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

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
    public partial class WindowsVirtualFilesDirectoryPlaceholderRepairPairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_WithWindowsVirtualFilesFullRunRepairsDirectoryPlaceholdersFromState()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            Guid docsNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            Guid reportsNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            stateStore.UpsertDirectory(syncPair, "Docs", docsNodeId);
            stateStore.UpsertDirectory(syncPair, "Docs/Reports", reportsNodeId);
            stateStore.UpsertFile(syncPair, "Docs/Reports/report.txt", reportsNodeId);
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            RecordingLocalChangeSuppression suppression = new RecordingLocalChangeSuppression();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            RecordingRunProgressPublisher progressPublisher = new RecordingRunProgressPublisher();
            WindowsVirtualFilesDirectoryPlaceholderRepairPairWork work = new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                inner,
                stateStore,
                cloudFiles,
                suppression,
                diagnostics,
                progressPublisher);

            await work.RunOnceAsync(syncPair, SyncRunRequest.Full);

            WindowsCloudFilesDiagnosticEvent repairEvent = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath),
                    Is.EqualTo(new[] { "Docs/Reports", "Docs" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RemoteDirectory.Id),
                    Is.EqualTo(new[] { reportsNodeId, docsNodeId }));
                Assert.That(
                    cloudFiles.SyncRootInSyncPairs.Select(static item => item.Id),
                    Is.EqualTo(new[] { syncPair.Id }));
                Assert.That(
                    suppression.BurstSuppressedRoots,
                    Is.EqualTo(new[] { syncPair.LocalRootPath }));
                Assert.That(
                    suppression.MetadataSuppressedWrites,
                    Is.EqualTo(new[]
                    {
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs/Reports"),
                        new SuppressedWrite(syncPair.Id, syncPair.LocalRootPath, "Docs"),
                    }));
                Assert.That(repairEvent.Operation, Is.EqualTo("repair-directory-placeholders"));
                Assert.That(repairEvent.Status, Is.EqualTo("completed"));
                Assert.That(repairEvent.RelativePath, Is.Null);
                Assert.That(repairEvent.Details, Does.Contain("candidates=2"));
                Assert.That(repairEvent.Details, Does.Contain("repaired=2"));
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
                        new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 0, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 1, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)2, IsCompleted = false },
                        new { Stage = SyncRunProgressStage.FinalizingCloudFiles, FilesCompleted = 2, FilesTotal = (int?)2, IsCompleted = true },
                    }));
            });
        }

        [Test]
        public async Task RunOnceAsync_WithScopedWindowsVirtualFilesRequestDoesNotRepairAllDirectories()
        {
            SyncPairSettings syncPair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            RecordingSyncPairWork inner = new RecordingSyncPairWork();
            FakeSyncStateStore stateStore = new FakeSyncStateStore();
            Guid docsNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            Guid reportsNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            Guid mediaNodeId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            Guid rawNodeId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            stateStore.UpsertDirectory(syncPair, "Docs", docsNodeId);
            stateStore.UpsertDirectory(syncPair, "Docs/Reports", reportsNodeId);
            stateStore.UpsertDirectory(syncPair, "Media", mediaNodeId);
            stateStore.UpsertDirectory(syncPair, "Media/Raw", rawNodeId);
            stateStore.UpsertDirectory(syncPair, "Unrelated", Guid.Parse("77777777-7777-7777-7777-777777777777"));
            RecordingCloudFilesAdapter cloudFiles = new RecordingCloudFilesAdapter();
            WindowsVirtualFilesDirectoryPlaceholderRepairPairWork work = new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork(
                inner,
                stateStore,
                cloudFiles);

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/Reports/report.txt", "Media"]));

            Assert.Multiple(() =>
            {
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RelativePath),
                    Is.EqualTo(new[] { "Docs/Reports", "Media/Raw", "Docs", "Media" }));
                Assert.That(
                    cloudFiles.DirectoryPlaceholders.Select(static request => request.RemoteDirectory.Id),
                    Is.EqualTo(new[] { reportsNodeId, rawNodeId, docsNodeId, mediaNodeId }));
                Assert.That(
                    cloudFiles.SyncRootInSyncPairs.Select(static item => item.Id),
                    Is.EqualTo(new[] { syncPair.Id }));
            });
        }

    }
}
