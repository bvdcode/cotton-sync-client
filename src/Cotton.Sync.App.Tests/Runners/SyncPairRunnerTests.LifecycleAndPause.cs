// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncPairRunnerTests
    {
        [Test]
        public async Task StartAsync_SetsIdleForEnabledPair()
        {
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true));

            await runner.StartAsync();

            Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
        }

        [Test]
        public async Task StartAsync_SetsDisabledForDisabledPair()
        {
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: false));

            await runner.StartAsync();

            Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Disabled));
        }

        [Test]
        public async Task StartAsync_ReportsMissingLocalRootInsteadOfIdle()
        {
            string missingRoot = Path.Combine(Path.GetTempPath(), "cotton-missing-root", Guid.NewGuid().ToString("N"));
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, localRootPath: missingRoot));

            await runner.StartAsync();

            Assert.Multiple(() =>
            {
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Does.Contain("cannot find the local sync folder"));
                Assert.That(runner.Status.CurrentOperation, Does.Contain("Action required"));
            });
        }

        [Test]
        public async Task PauseAndResumeAsync_UpdateState()
        {
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true));
            await runner.StartAsync();

            await runner.PauseAsync();
            SyncPairRunState pausedState = runner.Status.State;
            await runner.ResumeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(pausedState, Is.EqualTo(SyncPairRunState.Paused));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_RunsWorkAndReturnsIdle()
        {
            FakeSyncPairWork work = new FakeSyncPairWork();
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            SyncPairRunner runner = CreateRunner(syncPair, work);

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(work.LastSyncPair, Is.SameAs(syncPair));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Not.Null);
            });
        }

        [Test]
        public async Task SyncNowAsync_PreservesExplicitScopedRequest()
        {
            FakeSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(["Docs/report.txt"]);

            await runner.SyncNowAsync(request);

            Assert.That(work.LastRequest, Is.SameAs(request));
        }

        [Test]
        public async Task SyncNowAsync_LogsCompletionForSuccessfulScopedRequest()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(syncPair, logger: logger);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths([
                "Docs/report.txt",
                "Docs/report-renamed.txt",
            ]);

            await runner.SyncNowAsync(request);

            string[] messages = logger.Entries
                .Where(entry => entry.Level == LogLevel.Information)
                .Select(entry => entry.Message)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(messages, Has.Length.EqualTo(2));
                Assert.That(messages[0], Does.StartWith("Starting scoped sync"));
                Assert.That(messages[1], Does.StartWith("Completed scoped sync"));
                Assert.That(messages[1], Does.Contain(syncPair.Id.ToString()));
                Assert.That(messages[1], Does.Contain("requested paths=2"));
            });
        }

        [Test]
        public async Task SyncNowAsync_LogsRealtimeWindowsVirtualFilesRequestAsFeedPlanned()
        {
            SyncPairSettings syncPair = CreatePair(
                isEnabled: true,
                mode: SyncPairMode.WindowsVirtualFiles);
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(syncPair, logger: logger);

            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));

            string[] messages = logger.Entries
                .Where(entry => entry.Level == LogLevel.Information)
                .Select(entry => entry.Message)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(messages, Has.Length.EqualTo(2));
                Assert.That(messages[0], Does.StartWith("Starting feed-planned sync"));
                Assert.That(messages[1], Does.StartWith("Completed feed-planned sync"));
                Assert.That(messages, Has.None.Contains("full sync"));
            });
        }

        [Test]
        public async Task SyncNowAsync_StillLogsManualWindowsVirtualFilesRequestAsFull()
        {
            SyncPairSettings syncPair = CreatePair(
                isEnabled: true,
                mode: SyncPairMode.WindowsVirtualFiles);
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(syncPair, logger: logger);

            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Manual));

            string[] messages = logger.Entries
                .Where(entry => entry.Level == LogLevel.Information)
                .Select(entry => entry.Message)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(messages[0], Does.StartWith("Starting full sync"));
                Assert.That(messages[1], Does.StartWith("Completed full sync"));
            });
        }

        [Test]
        public async Task StartAsync_DoesNotMarkPairAsSuccessfullySynced()
        {
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true));

            await runner.StartAsync();

            Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Null);
        }

        [Test]
        public async Task SyncNowAsync_ExposesCurrentOperationWhileWorkRuns()
        {
            BlockingSyncPairWork work = new BlockingSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task syncTask = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));

            SyncPairStatus runningStatus = runner.Status;
            work.ReleaseCurrentRun();
            await syncTask;

            Assert.Multiple(() =>
            {
                Assert.That(runningStatus.State, Is.EqualTo(SyncPairRunState.Syncing));
                Assert.That(runningStatus.CurrentOperation, Is.EqualTo("Syncing changes"));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.CurrentOperation, Is.Null);
            });
        }

        [Test]
        public async Task SyncNowAsync_DoesNotRunWhenPaused()
        {
            FakeSyncPairWork work = new FakeSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            await runner.PauseAsync();

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.Zero);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
            });
        }

        [Test]
        public async Task PauseAsync_ClearsQueuedSyncRequest()
        {
            BlockingFirstRunSyncPairWork work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task firstSync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync();
            Task pause = runner.PauseAsync();

            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            await firstSync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
            });
        }

        [Test]
        public async Task SyncNowAsync_QueuesLocalChangeWhilePausedAndRunsItAfterResume()
        {
            FakeSyncPairWork work = new FakeSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            SyncRunRequest localChange = SyncRunRequest.ForLocalChangedPaths(["Docs/paused.txt"]);

            await runner.PauseAsync();
            await runner.SyncNowAsync(localChange);

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.Zero);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
            });

            await runner.ResumeAsync();
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[0].IsFull, Is.True);
                Assert.That(work.Requests[0].Causes, Is.EqualTo(SyncRunCause.Resume));
                Assert.That(work.Requests[1].IsFull, Is.False);
                Assert.That(work.Requests[1].Causes, Is.EqualTo(SyncRunCause.LocalChange));
                Assert.That(work.Requests[1].LocalChangedPaths, Is.EqualTo(new[] { "Docs/paused.txt" }));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_PromotesOversizedPausedScopeToBoundedFullRequest()
        {
            FakeSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            string[] changedPaths = Enumerable
                .Range(0, SyncRunRequest.MaximumQueuedScopedPaths + 1)
                .Select(static index => $"Docs/file-{index}.txt")
                .ToArray();

            await runner.PauseAsync();
            await runner.SyncNowAsync(SyncRunRequest.ForLocalChangedPaths(changedPaths));
            await runner.SyncNowAsync(SyncRunRequest.ForLocalChangedPaths(["Docs/later.txt"]));
            await runner.ResumeAsync();
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[0].Causes, Is.EqualTo(SyncRunCause.Resume));
                Assert.That(work.Requests[1].IsFull, Is.True);
                Assert.That(work.Requests[1].LocalChangedPaths, Is.Empty);
                Assert.That(
                    work.Requests[1].Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.LocalChangeOverflow));
            });
        }

        [Test]
        public async Task PauseAsync_PreservesLocalChangeArrivingWhileActiveWorkCancels()
        {
            BlockingFirstRunSyncPairWork work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            SyncRunRequest localChange = SyncRunRequest.ForLocalChangedPaths(["Docs/during-pause.txt"]);

            Task activeSync = runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Manual));
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task pause = runner.PauseAsync();
            await runner.SyncNowAsync(localChange);
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            await activeSync.WaitAsync(TimeSpan.FromSeconds(2));

            await runner.ResumeAsync();
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Resume));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(3));
                Assert.That(work.Requests[0].Causes, Is.EqualTo(SyncRunCause.Manual));
                Assert.That(work.Requests[1].Causes, Is.EqualTo(SyncRunCause.Resume));
                Assert.That(work.Requests[2].Causes, Is.EqualTo(SyncRunCause.LocalChange));
                Assert.That(work.Requests[2].LocalChangedPaths, Is.EqualTo(new[] { "Docs/during-pause.txt" }));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task PauseAsync_CancelsRunningSyncWorkAndPausesRunner()
        {
            CancellationObservingSyncPairWork work = new CancellationObservingSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task sync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task pause = runner.PauseAsync();
            bool cancellationObserved = await work.WaitForCancellationAsync(TimeSpan.FromSeconds(2));
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            await sync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(cancellationObserved, Is.True);
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
            });
        }

        [Test]
        public async Task PauseAsync_TreatsCancellationIOExceptionAsPaused()
        {
            CancellationSideEffectSyncPairWork work = new CancellationSideEffectSyncPairWork(new IOException("Transport was canceled."));
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, logger: logger);

            Task sync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task pause = runner.PauseAsync();
            bool cancellationObserved = await work.WaitForCancellationAsync(TimeSpan.FromSeconds(2));
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            await sync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(cancellationObserved, Is.True);
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
                Assert.That(logger.Entries.Select(entry => entry.Level), Does.Not.Contain(LogLevel.Error));
                Assert.That(
                    logger.Entries.Select(entry => entry.Message),
                    Has.Some.Contains("paused while in-flight work was canceling"));
            });
        }

    }
}
