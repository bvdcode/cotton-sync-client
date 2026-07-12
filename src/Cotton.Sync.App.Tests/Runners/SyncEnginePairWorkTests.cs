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
    public class SyncEnginePairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_MapsAppSyncPairToCoreSyncPair()
        {
            var engine = new FakeSyncEngine();
            var work = new SyncEnginePairWork(engine);
            var syncPair = new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
            };

            await work.RunOnceAsync(syncPair);

            Assert.Multiple(() =>
            {
                Assert.That(engine.RunOnceCallCount, Is.EqualTo(1));
                Assert.That(engine.LastPair, Is.Not.Null);
                Assert.That(engine.LastPair!.SyncPairId, Is.EqualTo(syncPair.Id.ToString("D")));
                Assert.That(engine.LastPair.LocalRootPath, Is.EqualTo(syncPair.LocalRootPath));
                Assert.That(engine.LastPair.RemoteRootNodeId, Is.EqualTo(syncPair.RemoteRootNodeId));
                Assert.That(engine.LastPair.MaterializationMode, Is.EqualTo(CoreSyncPairMaterializationMode.FullMirror));
            });
        }

        [Test]
        public async Task RunOnceAsync_MapsWindowsVirtualFilesModeToCoreMaterializationMode()
        {
            var engine = new FakeSyncEngine();
            var work = new SyncEnginePairWork(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            await work.RunOnceAsync(syncPair);

            Assert.That(engine.LastPair?.MaterializationMode, Is.EqualTo(CoreSyncPairMaterializationMode.WindowsVirtualFiles));
        }

        [Test]
        public async Task RunOnceAsync_MapsScopedRequestToCoreScope()
        {
            var engine = new FakeSyncEngine();
            var work = new SyncEnginePairWork(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.False);
                Assert.That(engine.LastOptions.Scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesCoreSyncActivities()
        {
            Guid syncPairId = Guid.NewGuid();
            var engine = new FakeSyncEngine
            {
                ActivityToReport = new CoreSyncActivity
                {
                    Kind = CoreSyncActivityKind.Conflict,
                    RelativePath = "Documents/report.txt",
                    Details = "Remote version saved as report conflict.txt",
                },
            };
            var publisher = new InMemoryAppActivityPublisher();
            var observer = new RecordingObserver<AppSyncActivity>();
            using IDisposable subscription = publisher.Subscribe(observer);
            var work = new SyncEnginePairWork(engine, publisher);
            SyncPairSettings syncPair = CreateSyncPair(syncPairId);

            await work.RunOnceAsync(syncPair);

            AppSyncActivity activity = observer.Values.Single();
            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions?.ActivityProgress, Is.Not.Null);
                Assert.That(activity.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(activity.Type, Is.EqualTo(SyncActivityKind.Conflict));
                Assert.That(activity.ItemPath, Is.EqualTo("Documents/report.txt"));
                Assert.That(activity.Message, Does.Contain("Created conflict copy Documents/report.txt"));
                Assert.That(activity.Message, Does.Contain("Remote version saved as report conflict.txt"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesCoreMoveActivities()
        {
            Guid syncPairId = Guid.NewGuid();
            var engine = new FakeSyncEngine
            {
                ActivityToReport = new CoreSyncActivity
                {
                    Kind = CoreSyncActivityKind.Moved,
                    RelativePath = "Documents/new-name.txt",
                    Details = "Moved from Documents/old-name.txt.",
                },
            };
            var publisher = new InMemoryAppActivityPublisher();
            var observer = new RecordingObserver<AppSyncActivity>();
            using IDisposable subscription = publisher.Subscribe(observer);
            var work = new SyncEnginePairWork(engine, publisher);
            SyncPairSettings syncPair = CreateSyncPair(syncPairId);

            await work.RunOnceAsync(syncPair);

            AppSyncActivity activity = observer.Values.Single();
            Assert.Multiple(() =>
            {
                Assert.That(activity.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(activity.Type, Is.EqualTo(SyncActivityKind.Moved));
                Assert.That(activity.ItemPath, Is.EqualTo("Documents/new-name.txt"));
                Assert.That(activity.Message, Does.Contain("Moved Documents/new-name.txt"));
                Assert.That(activity.Message, Does.Contain("Moved from Documents/old-name.txt."));
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesCorePlaceholderActivities()
        {
            Guid syncPairId = Guid.NewGuid();
            var engine = new FakeSyncEngine
            {
                ActivityToReport = new CoreSyncActivity
                {
                    Kind = CoreSyncActivityKind.PlaceholderCreated,
                    RelativePath = "Documents/cloud-only.txt",
                },
            };
            var publisher = new InMemoryAppActivityPublisher();
            var observer = new RecordingObserver<AppSyncActivity>();
            using IDisposable subscription = publisher.Subscribe(observer);
            var work = new SyncEnginePairWork(engine, publisher);
            SyncPairSettings syncPair = CreateSyncPair(syncPairId);

            await work.RunOnceAsync(syncPair);

            AppSyncActivity activity = observer.Values.Single();
            Assert.Multiple(() =>
            {
                Assert.That(activity.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(activity.Type, Is.EqualTo(SyncActivityKind.PlaceholderCreated));
                Assert.That(activity.ItemPath, Is.EqualTo("Documents/cloud-only.txt"));
                Assert.That(activity.Message, Does.Contain("Made cloud file available Documents/cloud-only.txt"));
                Assert.That(activity.Message, Does.Not.Contain("placeholder"));
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesCoreTransferProgress()
        {
            Guid syncPairId = Guid.NewGuid();
            var engine = new FakeSyncEngine
            {
                TransferProgressToReport = new CoreSyncTransferProgress(
                    CoreSyncTransferDirection.Upload,
                    "Documents/report.txt",
                    transferredBytes: 512,
                    totalBytes: 1024),
            };
            var publisher = new InMemoryAppTransferProgressPublisher();
            var observer = new RecordingObserver<AppSyncTransferProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            var work = new SyncEnginePairWork(engine, progressPublisher: publisher);
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
            var engine = new FakeSyncEngine
            {
                RunProgressToReport = new CoreSyncRunProgress(
                    CoreSyncRunProgressStage.ReconcilingFiles,
                    filesCompleted: 3,
                    filesTotal: 10,
                    currentPath: "Documents/report.txt",
                    startedAtUtc: new DateTime(2026, 6, 4, 9, 0, 0, DateTimeKind.Utc)),
            };
            var publisher = new InMemoryAppRunProgressPublisher();
            var observer = new RecordingObserver<AppSyncRunProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            var work = new SyncEnginePairWork(engine, runProgressPublisher: publisher);
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
            var engine = new FakeSyncEngine
            {
                RunProgressToReport = new CoreSyncRunProgress(
                    CoreSyncRunProgressStage.CreatingPlaceholders,
                    filesCompleted: 391_639,
                    filesTotal: 503_447,
                    currentPath: "Cloud/item.bin",
                    startedAtUtc: new DateTime(2026, 7, 10, 21, 44, 0, DateTimeKind.Utc)),
                Failure = new HttpRequestException("Bad Gateway"),
            };
            var publisher = new InMemoryAppRunProgressPublisher();
            var observer = new RecordingObserver<AppSyncRunProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            var work = new SyncEnginePairWork(engine, runProgressPublisher: publisher);

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

        [Test]
        public async Task RunOnceAsync_RetriesOnlyDeferredLocalPathsAfterQuietWindow()
        {
            CoreSyncRunResult deferredResult = new();
            deferredResult.RecordDeferredLocalPath("Docs/report.txt");
            FakeSyncEngine engine = new();
            engine.ResultsToReturn.Enqueue(deferredResult);
            engine.ResultsToReturn.Enqueue(new CoreSyncRunResult());
            List<TimeSpan> delays = [];
            SyncEnginePairWork work = new(
                engine,
                delayAsync: (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            SyncRunRequest request = SyncRunRequest.ForFull(SyncRunCause.InitialPopulation);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(engine.RunOnceCallCount, Is.EqualTo(2));
                Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(2) }));
                Assert.That(engine.OptionsHistory[0], Is.Null);
                Assert.That(engine.OptionsHistory[1]?.Scope.IsFull, Is.False);
                Assert.That(engine.OptionsHistory[1]?.Scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
            });
        }

        [Test]
        public void RunOnceAsync_ThrowsWhenCoreRunRequiresUserAction()
        {
            var engine = new FakeSyncEngine
            {
                ResultToReturn = new CoreSyncRunResult
                {
                    Activities =
                    {
                        new CoreSyncActivity
                        {
                            Kind = CoreSyncActivityKind.Skipped,
                            RelativePath = "Documents",
                            Details = "Remote delete blocked by mass-delete guard. 2 pending deletes exceed limit 1.",
                            RequiresUserAction = true,
                        },
                    },
                },
            };
            var work = new SyncEnginePairWork(engine);

            CoreSyncActionRequiredException? exception = Assert.ThrowsAsync<CoreSyncActionRequiredException>(
                async () => await work.RunOnceAsync(CreateSyncPair(Guid.NewGuid())));

            Assert.That(
                exception?.Message,
                Is.EqualTo("Remote delete blocked by mass-delete guard. 2 pending deletes exceed limit 1."));
        }

        private static SyncPairSettings CreateSyncPair(Guid id)
        {
            return new SyncPairSettings
            {
                Id = id,
                DisplayName = "Documents",
                LocalRootPath = "/home/user/Cotton",
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = true,
                Mode = SyncPairMode.FullMirror,
            };
        }

        private class FakeSyncEngine : CoreSyncEngine
        {
            public CoreSyncActivity? ActivityToReport { get; set; }

            public CoreSyncTransferProgress? TransferProgressToReport { get; set; }

            public CoreSyncRunProgress? RunProgressToReport { get; set; }

            public Exception? Failure { get; set; }

            public CoreSyncRunOptions? LastOptions { get; private set; }

            public List<CoreSyncRunOptions?> OptionsHistory { get; } = [];

            public CoreSyncPair? LastPair { get; private set; }

            public CoreSyncRunResult ResultToReturn { get; set; } = new();

            public Queue<CoreSyncRunResult> ResultsToReturn { get; } = [];

            public int RunOnceCallCount { get; private set; }

            public Task<CoreSyncRunResult> RunOnceAsync(
                CoreSyncPair syncPair,
                CoreSyncRunOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                RunOnceCallCount++;
                LastPair = syncPair;
                LastOptions = options;
                OptionsHistory.Add(options);
                if (ActivityToReport is not null)
                {
                    options?.ActivityProgress?.Report(ActivityToReport);
                }

                if (TransferProgressToReport is not null)
                {
                    options?.TransferProgress?.Report(TransferProgressToReport);
                }

                if (RunProgressToReport is not null)
                {
                    options?.RunProgress?.Report(RunProgressToReport);
                }

                if (Failure is not null)
                {
                    throw Failure;
                }

                CoreSyncRunResult result = ResultsToReturn.Count > 0
                    ? ResultsToReturn.Dequeue()
                    : ResultToReturn;
                return Task.FromResult(result);
            }
        }

    }
}
