// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Tests.TestSupport;
using AppSyncActivity = Cotton.Sync.App.Activities.AppSyncActivity;
using AppSyncRunProgress = Cotton.Sync.App.Progress.AppRunProgress;
using AppSyncTransferProgress = Cotton.Sync.App.Progress.AppTransferProgress;
using CoreSyncActivity = Cotton.Sync.SyncActivity;
using CoreSyncActivityKind = Cotton.Sync.SyncActivityKind;
using CoreSyncEngine = Cotton.Sync.ISyncEngine;
using CoreSyncPair = Cotton.Sync.SyncPair;
using CoreSyncPairMaterializationMode = Cotton.Sync.SyncPairMaterializationMode;
using CoreSyncRunProgress = Cotton.Sync.SyncRunProgress;
using CoreSyncRunProgressStage = Cotton.Sync.SyncRunProgressStage;
using CoreSyncRunOptions = Cotton.Sync.SyncRunOptions;
using CoreSyncRunResult = Cotton.Sync.SyncRunResult;
using CoreSyncTransferDirection = Cotton.Sync.SyncTransferDirection;
using CoreSyncTransferProgress = Cotton.Sync.SyncTransferProgress;
using CoreSyncActionRequiredException = Cotton.Sync.SyncActionRequiredException;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncEnginePairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_PublishesCoreTransferProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeSyncEngine engine = new FakeSyncEngine
            {
                TransferProgressToReport = new CoreSyncTransferProgress(
                    CoreSyncTransferDirection.Upload,
                    "Documents/report.txt",
                    transferredBytes: 512,
                    totalBytes: 1024),
            };
            InMemoryAppTransferProgressPublisher publisher = new InMemoryAppTransferProgressPublisher();
            RecordingObserver<AppSyncTransferProgress> observer = new RecordingObserver<AppSyncTransferProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new SyncEnginePairWork(engine, progressPublisher: publisher);
            SyncPairSettings syncPair = CreateSyncPair(syncPairId);

            await work.RunOnceAsync(syncPair);

            AppSyncTransferProgress progress = observer.Values[0];
            AppSyncTransferProgress completed = observer.Values[1];
            Assert.Multiple(() =>
            {
                Assert.That(observer.Values, Has.Count.EqualTo(2));
                Assert.That(engine.LastOptions?.TransferProgress, Is.Not.Null);
                Assert.That(progress.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(progress.Direction, Is.EqualTo(SyncTransferDirection.Upload));
                Assert.That(progress.RelativePath, Is.EqualTo("Documents/report.txt"));
                Assert.That(progress.TransferredBytes, Is.EqualTo(512));
                Assert.That(progress.TotalBytes, Is.EqualTo(1024));
                Assert.That(progress.IsCompleted, Is.False);
                Assert.That(completed.IsCompleted, Is.True);
                Assert.That(completed.TransferredBytes, Is.EqualTo(512));
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesCoreRunProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeSyncEngine engine = new FakeSyncEngine
            {
                RunProgressToReport = new CoreSyncRunProgress(
                    CoreSyncRunProgressStage.ReconcilingFiles,
                    filesCompleted: 3,
                    filesTotal: 10,
                    currentPath: "Documents/report.txt",
                    startedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)),
            };
            InMemoryAppRunProgressPublisher publisher = new InMemoryAppRunProgressPublisher();
            RecordingObserver<AppSyncRunProgress> observer = new RecordingObserver<AppSyncRunProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new SyncEnginePairWork(engine, runProgressPublisher: publisher);
            SyncPairSettings syncPair = CreateSyncPair(syncPairId);

            await work.RunOnceAsync(syncPair);

            AppSyncRunProgress progress = observer.Values[0];
            AppSyncRunProgress completed = observer.Values[1];
            Assert.Multiple(() =>
            {
                Assert.That(observer.Values, Has.Count.EqualTo(2));
                Assert.That(engine.LastOptions?.RunProgress, Is.Not.Null);
                Assert.That(progress.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(progress.Stage, Is.EqualTo(SyncRunProgressStage.ReconcilingFiles));
                Assert.That(progress.FilesCompleted, Is.EqualTo(3));
                Assert.That(progress.FilesTotal, Is.EqualTo(10));
                Assert.That(progress.CurrentPath, Is.EqualTo("Documents/report.txt"));
                Assert.That(progress.IsCompleted, Is.False);
                Assert.That(progress.Causes, Is.EqualTo(SyncRunCause.Manual));
                Assert.That(progress.IsFull, Is.True);
                Assert.That(progress.RequestedPathCount, Is.Zero);
                Assert.That(completed.Stage, Is.EqualTo(SyncRunProgressStage.Completed));
                Assert.That(completed.IsCompleted, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesFullRunProgressWithoutRequestedPathCount()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeSyncEngine engine = new()
            {
                RunProgressToReport = new CoreSyncRunProgress(
                    CoreSyncRunProgressStage.ReconcilingFiles,
                    filesCompleted: 1,
                    filesTotal: 2,
                    currentPath: "Documents/report.txt",
                    startedAtUtc: new DateTime(2026, 7, 11, 23, 0, 0, DateTimeKind.Utc)),
            };
            InMemoryAppRunProgressPublisher publisher = new();
            RecordingObserver<AppSyncRunProgress> observer = new();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new(engine, runProgressPublisher: publisher);
            SyncPairSettings syncPair = CreateSyncPair(syncPairId);
            SyncRunRequest request = SyncRunRequest
                .ForFull(SyncRunCause.Manual)
                .Merge(SyncRunRequest.ForLocalChangedPaths(["Documents/report.txt"]));

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(observer.Values, Has.Count.EqualTo(2));
                Assert.That(observer.Values.Select(static progress => progress.IsFull), Is.All.True);
                Assert.That(observer.Values.Select(static progress => progress.RequestedPathCount), Is.All.EqualTo(0));
            });
        }

        [Test]
        public void RunOnceAsync_PublishesTerminalRunProgressWhenCoreFails()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeSyncEngine engine = new FakeSyncEngine
            {
                RunProgressToReport = new CoreSyncRunProgress(
                    CoreSyncRunProgressStage.CreatingPlaceholders,
                    filesCompleted: 391_639,
                    filesTotal: 503_447,
                    currentPath: "Cloud/item.bin",
                    startedAtUtc: new DateTime(2026, 7, 10, 21, 44, 0, DateTimeKind.Utc)),
                Failure = new HttpRequestException("Bad Gateway"),
            };
            InMemoryAppRunProgressPublisher publisher = new InMemoryAppRunProgressPublisher();
            RecordingObserver<AppSyncRunProgress> observer = new RecordingObserver<AppSyncRunProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new SyncEnginePairWork(engine, runProgressPublisher: publisher);

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await work.RunOnceAsync(CreateSyncPair(syncPairId)));

            Assert.Multiple(() =>
            {
                Assert.That(observer.Values, Has.Count.EqualTo(2));
                Assert.That(observer.Values[0].IsCompleted, Is.False);
                Assert.That(observer.Values[1].Stage, Is.EqualTo(SyncRunProgressStage.Completed));
                Assert.That(observer.Values[1].IsCompleted, Is.True);
                Assert.That(observer.Values[1].FilesCompleted, Is.EqualTo(391_639));
            });
        }

    }
}
