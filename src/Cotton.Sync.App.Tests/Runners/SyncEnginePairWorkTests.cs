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
        public async Task RunOnceAsync_MapsAppSyncPairToCoreSyncPair()
        {
            FakeSyncEngine engine = new FakeSyncEngine();
            SyncEnginePairWork work = new SyncEnginePairWork(engine);
            SyncPairSettings syncPair = new SyncPairSettings
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
            FakeSyncEngine engine = new FakeSyncEngine();
            SyncEnginePairWork work = new SyncEnginePairWork(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            await work.RunOnceAsync(syncPair);

            Assert.That(engine.LastPair?.MaterializationMode, Is.EqualTo(CoreSyncPairMaterializationMode.WindowsVirtualFiles));
        }

        [Test]
        public async Task RunOnceAsync_MapsScopedRequestToCoreScope()
        {
            FakeSyncEngine engine = new FakeSyncEngine();
            SyncEnginePairWork work = new SyncEnginePairWork(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForLocalChangedPaths(
                    ["Docs/report.txt", "Docs/deleted.txt"],
                    ["Docs/deleted.txt"]));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.False);
                Assert.That(engine.LastOptions.Scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/deleted.txt", "Docs/report.txt" }));
                Assert.That(engine.LastOptions.Scope.LocalDeletedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
            });
        }

        [Test]
        public async Task RunOnceAsync_MapsExplicitRemoteDeleteApprovalToCoreOptions()
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            RemoteDeletePlanApproval approval = new(101, new string('a', 64));

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.Manual, approval));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.ApprovedRemoteDeletePlan, Is.EqualTo(approval));
            });
        }

        [Test]
        public async Task RunOnceAsync_DisablesStreamingFastPathForRemoteChangeFullRequest()
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            SyncRunRequest request = SyncRunRequest.ForFull(
                SyncRunCause.Manual | SyncRunCause.RealtimeRemoteChange);

            await work.RunOnceAsync(syncPair, request);

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.True);
                Assert.That(engine.LastOptions.AllowInitialVirtualFilesStreaming, Is.False);
            });
        }

        [TestCase(SyncRunCause.Periodic)]
        [TestCase(SyncRunCause.Resume)]
        public async Task RunOnceAsync_DisablesStreamingFastPathForLocalSafetyFullRequest(SyncRunCause cause)
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());

            await work.RunOnceAsync(syncPair, SyncRunRequest.ForFull(cause));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.True);
                Assert.That(engine.LastOptions.AllowInitialVirtualFilesStreaming, Is.False);
            });
        }

        [Test]
        public async Task RunOnceAsync_EnablesMissingPlaceholderRecoveryForInitialPopulationRequest()
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.InitialPopulation));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.True);
                Assert.That(engine.LastOptions.RestoreMissingRemoteOnlyPlaceholders, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_UsesStreamingFastPathForInitialPopulationResume()
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(SyncRunCause.InitialPopulation | SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.True);
                Assert.That(engine.LastOptions.AllowInitialVirtualFilesStreaming, Is.True);
                Assert.That(engine.LastOptions.RestoreMissingRemoteOnlyPlaceholders, Is.True);
            });
        }

        [Test]
        public async Task RunOnceAsync_PublishesCoreSyncActivities()
        {
            Guid syncPairId = Guid.NewGuid();
            FakeSyncEngine engine = new FakeSyncEngine
            {
                ActivityToReport = new CoreSyncActivity
                {
                    Kind = CoreSyncActivityKind.Conflict,
                    RelativePath = "Documents/report.txt",
                    Details = "Remote version saved as report conflict.txt",
                },
            };
            InMemoryAppActivityPublisher publisher = new InMemoryAppActivityPublisher();
            RecordingObserver<AppSyncActivity> observer = new RecordingObserver<AppSyncActivity>();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new SyncEnginePairWork(engine, publisher);
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
            FakeSyncEngine engine = new FakeSyncEngine
            {
                ActivityToReport = new CoreSyncActivity
                {
                    Kind = CoreSyncActivityKind.Moved,
                    RelativePath = "Documents/new-name.txt",
                    Details = "Moved from Documents/old-name.txt.",
                },
            };
            InMemoryAppActivityPublisher publisher = new InMemoryAppActivityPublisher();
            RecordingObserver<AppSyncActivity> observer = new RecordingObserver<AppSyncActivity>();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new SyncEnginePairWork(engine, publisher);
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
            FakeSyncEngine engine = new FakeSyncEngine
            {
                ActivityToReport = new CoreSyncActivity
                {
                    Kind = CoreSyncActivityKind.PlaceholderCreated,
                    RelativePath = "Documents/cloud-only.txt",
                },
            };
            InMemoryAppActivityPublisher publisher = new InMemoryAppActivityPublisher();
            RecordingObserver<AppSyncActivity> observer = new RecordingObserver<AppSyncActivity>();
            using IDisposable subscription = publisher.Subscribe(observer);
            SyncEnginePairWork work = new SyncEnginePairWork(engine, publisher);
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
                Assert.That(engine.OptionsHistory[0]?.RestoreMissingRemoteOnlyPlaceholders, Is.True);
                Assert.That(engine.OptionsHistory[1]?.Scope.IsFull, Is.False);
                Assert.That(engine.OptionsHistory[1]?.Scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(engine.OptionsHistory[1]?.RestoreMissingRemoteOnlyPlaceholders, Is.False);
            });
        }

        [Test]
        public void RunOnceAsync_StopsAfterMaximumDeferredLocalRetries()
        {
            CoreSyncRunResult deferredResult = new();
            deferredResult.RecordDeferredLocalPath("Docs/locked.txt");
            FakeSyncEngine engine = new()
            {
                ResultToReturn = deferredResult,
            };
            List<TimeSpan> delays = [];
            SyncEnginePairWork work = new(
                engine,
                delayAsync: (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });

            CoreSyncActionRequiredException? exception = Assert.ThrowsAsync<CoreSyncActionRequiredException>(
                async () => await work.RunOnceAsync(
                    CreateSyncPair(Guid.NewGuid()),
                    SyncRunRequest.ForFull(SyncRunCause.LocalChange)));

            Assert.Multiple(() =>
            {
                Assert.That(engine.RunOnceCallCount, Is.EqualTo(4));
                Assert.That(delays, Has.Count.EqualTo(3));
                Assert.That(exception?.Message, Does.Contain("Docs/locked.txt"));
            });
        }

        [Test]
        public void RunOnceAsync_ThrowsWhenCoreRunRequiresUserAction()
        {
            FakeSyncEngine engine = new FakeSyncEngine
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
            SyncEnginePairWork work = new SyncEnginePairWork(engine);

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
