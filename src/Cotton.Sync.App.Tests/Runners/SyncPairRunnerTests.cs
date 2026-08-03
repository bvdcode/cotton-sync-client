// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Tests.Runners
{
    public class SyncPairRunnerTests
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
            var work = new FakeSyncPairWork();
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
            var logger = new RecordingLogger<SyncPairRunner>();
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
            var logger = new RecordingLogger<SyncPairRunner>();
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
            var logger = new RecordingLogger<SyncPairRunner>();
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
            var work = new BlockingSyncPairWork();
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
            var work = new FakeSyncPairWork();
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
            var work = new BlockingFirstRunSyncPairWork();
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
            var work = new FakeSyncPairWork();
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
        public async Task PauseAsync_PreservesLocalChangeArrivingWhileActiveWorkCancels()
        {
            var work = new BlockingFirstRunSyncPairWork();
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
            var work = new CancellationObservingSyncPairWork();
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
            var work = new CancellationSideEffectSyncPairWork(new IOException("Transport was canceled."));
            var logger = new RecordingLogger<SyncPairRunner>();
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

        [Test]
        public async Task StopAsync_ClearsQueuedSyncRequest()
        {
            var work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task firstSync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync();
            Task stop = runner.StopAsync();
            work.ReleaseRun();

            OperationCanceledException? exception = Assert.CatchAsync<OperationCanceledException>(
                async () => await firstSync);
            await stop.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Disabled));
            });
        }

        [Test]
        public async Task StopAsync_CancelsRunningSyncWorkAndDisablesRunner()
        {
            var work = new CancellationObservingSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task sync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task stop = runner.StopAsync();
            bool cancellationObserved = await work.WaitForCancellationAsync(TimeSpan.FromSeconds(2));
            OperationCanceledException? exception = Assert.CatchAsync<OperationCanceledException>(
                async () => await sync);
            await stop.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(cancellationObserved, Is.True);
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Disabled));
            });
        }

        [Test]
        public async Task StopAsync_TreatsCancellationIOExceptionAsCancellationAndDisablesRunner()
        {
            var work = new CancellationSideEffectSyncPairWork(new IOException("Transport was canceled."));
            var logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, logger: logger);

            Task sync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task stop = runner.StopAsync();
            bool cancellationObserved = await work.WaitForCancellationAsync(TimeSpan.FromSeconds(2));
            OperationCanceledException? exception = Assert.CatchAsync<OperationCanceledException>(
                async () => await sync);
            await stop.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(cancellationObserved, Is.True);
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.InnerException, Is.TypeOf<IOException>());
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Disabled));
                Assert.That(logger.Entries.Select(entry => entry.Level), Does.Not.Contain(LogLevel.Error));
                Assert.That(
                    logger.Entries.Select(entry => entry.Message),
                    Has.Some.Contains("stopped while in-flight work was canceling"));
            });
        }

        [Test]
        public async Task PauseAsync_WhenCanceledBeforeStateChange_DoesNotBlockFutureSyncRequests()
        {
            var work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            using var cancellation = new CancellationTokenSource();

            Task firstSync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();

            OperationCanceledException? exception = Assert.CatchAsync<OperationCanceledException>(
                async () => await runner.PauseAsync(cancellation.Token));
            work.ReleaseRun();
            await firstSync.WaitAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task StopAsync_WhenCanceledBeforeStateChange_DoesNotBlockFutureSyncRequests()
        {
            var work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            using var cancellation = new CancellationTokenSource();

            Task firstSync = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();

            OperationCanceledException? exception = Assert.CatchAsync<OperationCanceledException>(
                async () => await runner.StopAsync(cancellation.Token));
            work.ReleaseRun();
            await firstSync.WaitAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public void SyncNowAsync_SetsErrorAndRethrowsOnFailure()
        {
            var work = new FakeSyncPairWork
            {
                Failure = new InvalidOperationException("sync failed"),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo("sync failed"));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsRemoteQuotaAsActionRequiredMessage()
        {
            var work = new FakeSyncPairWork
            {
                Failure = new CottonApiException(
                    (System.Net.HttpStatusCode)507,
                    null,
                    "Cotton API request failed with status 507."),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            CottonApiException? exception = Assert.ThrowsAsync<CottonApiException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Remote storage quota exceeded. Free space in Cotton Cloud or choose a smaller sync folder.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsLocalPermissionDeniedAsActionRequiredMessage()
        {
            var work = new FakeSyncPairWork
            {
                Failure = new UnauthorizedAccessException("Access to the path was denied."),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            UnauthorizedAccessException? exception = Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Permission denied while accessing local sync files. Check folder permissions and retry.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsLocalDiskFullAsActionRequiredMessage()
        {
            var work = new FakeSyncPairWork
            {
                Failure = new TestIOException(unchecked((int)0x80070070)),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            IOException? exception = Assert.ThrowsAsync<TestIOException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Local disk is full. Free space on this computer and retry sync.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsPreflightLocalDiskFullAsActionRequiredMessage()
        {
            var work = new FakeSyncPairWork
            {
                Failure = new LocalInsufficientDiskSpaceException("Videos/big.bin", requiredBytes: 200, availableBytes: 100),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            LocalInsufficientDiskSpaceException? exception = Assert.ThrowsAsync<LocalInsufficientDiskSpaceException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Local disk is full. Free space on this computer and retry sync.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [TestCase(System.Net.HttpStatusCode.InternalServerError)]
        [TestCase(System.Net.HttpStatusCode.ServiceUnavailable)]
        [TestCase(System.Net.HttpStatusCode.Locked)]
        public async Task SyncNowAsync_RetriesTransientServerFailureAndReturnsIdleOnRecovery(System.Net.HttpStatusCode statusCode)
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("server unavailable", null, statusCode),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_RetriesRateLimitAndReturnsIdleOnRecovery()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [TestCase(
            System.Net.HttpStatusCode.Unauthorized,
            "Session expired. Sign in again to continue syncing.",
            TestName = "SyncNowAsync_ReportsExpiredSessionAsActionRequiredMessage")]
        [TestCase(
            System.Net.HttpStatusCode.Forbidden,
            "Cotton Cloud denied access to this sync folder. Check account permissions and sign in again if needed.",
            TestName = "SyncNowAsync_ReportsForbiddenServerResponseAsActionRequiredMessage")]
        [TestCase(
            System.Net.HttpStatusCode.Conflict,
            "Cotton Cloud reported a conflict while syncing. Review conflicts and retry.",
            TestName = "SyncNowAsync_ReportsServerConflictAsActionRequiredMessage")]
        public void SyncNowAsync_ReportsNonRetriableServerResponseAsActionRequiredMessage(
            System.Net.HttpStatusCode statusCode,
            string expected)
        {
            var work = new FakeSyncPairWork
            {
                Failure = new CottonApiException(
                    statusCode,
                    "{\"success\":false,\"message\":\"server rejected sync\"}",
                    "Cotton API request failed with status " + (int)statusCode + "."),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            CottonApiException? exception = Assert.ThrowsAsync<CottonApiException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public async Task SyncNowAsync_RetriesHttpTimeoutAndReturnsIdleOnRecovery()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_RetriesUnavailableLocalFileAndReturnsIdleOnRecovery()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new LocalFileUnavailableException(
                        "writing.txt",
                        "/home/user/Cotton/writing.txt",
                        "the file changed during scanning."),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_WaitsForPersistentlyLockedLocalFileWithoutActionRequiredAndRecovers()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "locked.txt");
            File.WriteAllText(filePath, "locked");
            FileStream? locked = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unavailable = new LocalFileUnavailableException(
                "locked.txt",
                filePath,
                new IOException("The file is being used by another process."));
            var work = new FakeSyncPairWork
            {
                Failures = [unavailable, unavailable],
            };
            var retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(10),
            };
            var logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, retryOptions, logger);

            try
            {
                Task sync = runner.SyncNowAsync();
                for (int attempt = 0; attempt < 200 && runner.Status.State != SyncPairRunState.Waiting; attempt++)
                {
                    await Task.Delay(5);
                }

                SyncPairStatus waiting = runner.Status;
                Assert.Multiple(() =>
                {
                    Assert.That(sync.IsCompleted, Is.False);
                    Assert.That(waiting.State, Is.EqualTo(SyncPairRunState.Waiting));
                    Assert.That(waiting.LastError, Does.Contain("locked.txt"));
                    Assert.That(waiting.CurrentOperation, Does.Not.StartWith("Action required"));
                    Assert.That(
                        logger.Entries.Select(entry => entry.Message),
                        Has.None.Contains("Sync pair runner failed"));
                });

                locked.Dispose();
                locked = null;
                await sync.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(3));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                    Assert.That(runner.Status.LastError, Is.Null);
                    Assert.That(runner.Status.CurrentOperation, Is.Null);
                });
            }
            finally
            {
                locked?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task PauseAsync_CancelsLocalFileWaitAndPausesRunner()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "locked.txt");
            File.WriteAllText(filePath, "locked");
            FileStream? locked = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unavailable = new LocalFileUnavailableException(
                "locked.txt",
                filePath,
                new IOException("The file is being used by another process."));
            var work = new FakeSyncPairWork
            {
                Failures = [unavailable],
            };
            var retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 1,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, retryOptions);

            try
            {
                Task sync = runner.SyncNowAsync();
                for (int attempt = 0; attempt < 200 && runner.Status.State != SyncPairRunState.Waiting; attempt++)
                {
                    await Task.Delay(5);
                }

                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Waiting));

                await runner.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await sync.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(1));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
                    Assert.That(runner.Status.LastError, Is.Null);
                });
            }
            finally
            {
                locked.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SyncNowAsync_RetriesLockedLocalFileAfterItBecomesReadable()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "locked.txt");
            File.WriteAllText(filePath, "locked");
            FileStream? locked = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var work = new ReleasingLockedFileSyncPairWork(() =>
            {
                locked?.Dispose();
                locked = null;
            });
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, NoDelayRetryOptions());

            try
            {
                await runner.SyncNowAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(2));
                    Assert.That(work.ScannedPaths, Is.EqualTo(new[] { "locked.txt" }));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                });
            }
            finally
            {
                locked?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task SyncNowAsync_RetriesMissingLocalRootAndReturnsIdleOnRecovery()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-runner-tests", Guid.NewGuid().ToString("N"));
            var work = new RestoringMissingRootSyncPairWork(root, () =>
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "restored.txt"), "restored");
            });
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true, root), work, NoDelayRetryOptions());

            try
            {
                await runner.SyncNowAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(work.RunCount, Is.EqualTo(2));
                    Assert.That(work.ScannedPaths, Is.EqualTo(new[] { "restored.txt" }));
                    Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                    Assert.That(runner.Status.LastError, Is.Null);
                });
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Test]
        public void SyncNowAsync_ReportsMissingLocalRootAsActionRequiredMessageWhenRestoreDoesNotHappen()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new DirectoryNotFoundException("Local sync root was not found: W:\\local"),
                    new DirectoryNotFoundException("Local sync root was not found: W:\\local"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            DirectoryNotFoundException? exception = Assert.ThrowsAsync<DirectoryNotFoundException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Cotton Sync cannot find the local sync folder. Restore or reconnect the folder, then retry sync.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_SetsOfflineAndRethrowsWhenTransientNetworkFailurePersists()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("network down"),
                    new HttpRequestException("network down"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            HttpRequestException? exception = Assert.ThrowsAsync<HttpRequestException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
            });
        }

        [Test]
        public async Task SyncNowAsync_UsesCappedExponentialBackoffForNetworkTimeouts()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("request timed out", new TimeoutException()),
                    new HttpRequestException("request timed out", new TimeoutException()),
                    new HttpRequestException("request timed out", new TimeoutException()),
                ],
            };
            var logger = new RecordingLogger<SyncPairRunner>();
            var retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 4,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, retryOptions, logger);

            await runner.SyncNowAsync();

            string[] retryMessages = logger.Entries
                .Where(entry => entry.Level == LogLevel.Warning)
                .Select(entry => entry.Message)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(4));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(retryMessages, Has.Length.EqualTo(3));
                Assert.That(retryMessages[0], Does.Contain("after 00:00:00.001"));
                Assert.That(retryMessages[1], Does.Contain("after 00:00:00.002"));
                Assert.That(retryMessages[2], Does.Contain("after 00:00:00.002"));
            });
        }

        [Test]
        public async Task PauseAsync_CancelsNetworkRetryBackoffPromptly()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("request timed out", new TimeoutException()),
                ],
            };
            var retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(30),
                MaxDelay = TimeSpan.FromSeconds(30),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, retryOptions);

            Task activeSync = runner.SyncNowAsync();
            Assert.Multiple(() =>
            {
                Assert.That(activeSync.IsCompleted, Is.False);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
            });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await runner.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await activeSync.WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            Assert.Multiple(() =>
            {
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
                Assert.That(runner.Status.LastError, Is.Null);
            });
        }

        [TestCase(System.Net.HttpStatusCode.InternalServerError)]
        [TestCase(System.Net.HttpStatusCode.BadGateway)]
        [TestCase(System.Net.HttpStatusCode.ServiceUnavailable)]
        [TestCase(System.Net.HttpStatusCode.GatewayTimeout)]
        public async Task SyncNowAsync_RetriesTransientApiFailureAndReturnsIdleOnRecovery(
            System.Net.HttpStatusCode statusCode)
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new CottonApiException(statusCode, "temporary server response", "request failed"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.LastError, Is.Null);
            });
        }

        [Test]
        public void SyncNowAsync_TreatsMissingChangeFeedRouteAsTemporaryOfflineState()
        {
            var failure = new RemoteChangeFeedUnavailableException(
                new CottonApiException(
                    System.Net.HttpStatusCode.NotFound,
                    "404 page not found",
                    "Cotton API request GET /api/v1/sync/changes failed with status 404."));
            var work = new FakeSyncPairWork
            {
                Failures = [failure, failure],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            RemoteChangeFeedUnavailableException? exception = Assert.ThrowsAsync<RemoteChangeFeedUnavailableException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud desktop change feed is temporarily unavailable. Cotton Sync will retry automatically."));
                Assert.That(runner.Status.CurrentOperation, Does.StartWith("Waiting for connection:"));
            });
        }

        [Test]
        public void SyncNowAsync_TreatsPersistentBadGatewayAsTemporaryOfflineState()
        {
            var failure = new CottonApiException(
                System.Net.HttpStatusCode.BadGateway,
                "502 Bad Gateway",
                "Cotton API request failed with status 502.");
            var work = new FakeSyncPairWork
            {
                Failures = [failure, failure],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            CottonApiException? exception = Assert.ThrowsAsync<CottonApiException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
                Assert.That(runner.Status.CurrentOperation, Does.StartWith("Waiting for connection:"));
            });
        }

        [Test]
        public void SyncNowAsync_TreatsGenericProxyNotFoundAsTemporaryOfflineState()
        {
            var failure = new CottonApiException(
                System.Net.HttpStatusCode.NotFound,
                "404 page not found",
                "Cotton API request failed with status 404.");
            var work = new FakeSyncPairWork
            {
                Failures = [failure, failure],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            Assert.ThrowsAsync<CottonApiException>(async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
            });
        }

        [Test]
        public void SyncNowAsync_KeepsStructuredNotFoundAsActionRequiredState()
        {
            var failure = new CottonApiException(
                System.Net.HttpStatusCode.NotFound,
                "{\"error\":\"node not found\"}",
                "Cotton API request failed with status 404.");
            var work = new FakeSyncPairWork
            {
                Failure = failure,
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            Assert.ThrowsAsync<CottonApiException>(async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
            });
        }

        [Test]
        public async Task SyncNowAsync_ReturnsFromOfflineToIdleWhenNetworkRecovers()
        {
            var work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("network down"),
                    new HttpRequestException("network down"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await runner.SyncNowAsync());
            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(3));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.CurrentOperation, Is.Null);
                Assert.That(runner.Status.LastError, Is.Null);
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Not.Null);
            });
        }

        [Test]
        public void SyncNowAsync_FailureLogIncludesSyncPairId()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            var logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(
                syncPair,
                new FakeSyncPairWork { Failure = new InvalidOperationException("sync failed") },
                logger: logger);

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runner.SyncNowAsync());

            Assert.That(
                logger.Entries.Select(entry => entry.Message),
                Has.Some.Contains(syncPair.Id.ToString()));
        }

        [Test]
        public async Task SyncNowAsync_CoalescesOverlappingRequestsIntoOneQueuedRun()
        {
            var work = new BlockingSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task first = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task second = runner.SyncNowAsync();
            Task third = runner.SyncNowAsync();

            await Task.WhenAll(second, third);
            work.ReleaseCurrentRun();
            await work.WaitForRunCountAsync(2, TimeSpan.FromSeconds(2));
            work.ReleaseCurrentRun();
            await first;

            Assert.That(work.RunCount, Is.EqualTo(2));
        }

        [Test]
        public async Task SyncNowAsync_CoalescesQueuedRealtimeAndPeriodicFullRequests()
        {
            BlockingSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task activeSync = runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Manual));
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            work.ReleaseCurrentRun();
            await work.WaitForRunCountAsync(2, TimeSpan.FromSeconds(2));
            work.ReleaseCurrentRun();
            await activeSync;

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[1].IsFull, Is.True);
                Assert.That(
                    work.Requests[1].Causes,
                    Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.RealtimeRemoteChange));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_CoalescesRepeatedScopedEditsWhileAnotherUploadIsActive()
        {
            BlockingSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            SyncRunRequest blockerRequest = SyncRunRequest.ForLocalChangedPaths(["active-upload-blocker.bin"]);
            SyncRunRequest pinnedEditRequest = SyncRunRequest.ForLocalChangedPaths(["pinned-edit.bin"]);

            Task activeSync = runner.SyncNowAsync(blockerRequest);
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(pinnedEditRequest);
            await runner.SyncNowAsync(pinnedEditRequest);
            await runner.SyncNowAsync(pinnedEditRequest);

            work.ReleaseCurrentRun();
            await work.WaitForRunCountAsync(2, TimeSpan.FromSeconds(2));
            work.ReleaseCurrentRun();
            await activeSync;

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "active-upload-blocker.bin" }));
                Assert.That(work.Requests[1].LocalChangedPaths, Is.EqualTo(new[] { "pinned-edit.bin" }));
                Assert.That(work.Requests[1].IsFull, Is.False);
                Assert.That(work.Requests[1].Causes, Is.EqualTo(SyncRunCause.LocalChange));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_MergesQueuedScopedRequestsIntoLaterFullCheck()
        {
            var work = new BlockingFirstFailureSyncPairWork();
            SyncPairRunner runner = CreateRunner(
                CreatePair(isEnabled: true),
                work,
                NoDelayRetryOptions(maxAttempts: 1));
            SyncRunRequest firstRequest = SyncRunRequest.ForLocalChangedPaths(["Docs/first.txt"]);
            SyncRunRequest secondRequest = SyncRunRequest.ForLocalChangedPaths(["Docs/second.txt"]);

            Task first = runner.SyncNowAsync(firstRequest);
            await work.WaitForFirstRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(secondRequest);
            work.ReleaseFirstRun();
            Assert.ThrowsAsync<HttpRequestException>(async () => await first);

            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(3));
                Assert.That(work.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/first.txt" }));
                Assert.That(work.Requests[1].LocalChangedPaths, Is.EqualTo(new[] { "Docs/first.txt" }));
                Assert.That(work.Requests[2].IsFull, Is.True);
                Assert.That(work.Requests[2].Causes, Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.LocalChange));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        private static SyncPairRunner CreateRunner(
            SyncPairSettings syncPair,
            ISyncPairWork? work = null,
            SyncPairRunnerRetryOptions? retryOptions = null,
            ILogger<SyncPairRunner>? logger = null)
        {
            return new SyncPairRunner(syncPair, work ?? new FakeSyncPairWork(), retryOptions, logger);
        }

        private static SyncPairRunnerRetryOptions NoDelayRetryOptions(int maxAttempts = 3)
        {
            return new SyncPairRunnerRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
            };
        }

        private static SyncPairSettings CreatePair(
            bool isEnabled,
            string? localRootPath = null,
            SyncPairMode mode = SyncPairMode.FullMirror)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = localRootPath ?? Path.GetTempPath(),
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Documents",
                IsEnabled = isEnabled,
                Mode = mode,
            };
        }

        private class FakeSyncPairWork : ISyncPairWork
        {
            private readonly Queue<Exception> _failures = [];

            public Exception? Failure { get; set; }

            public IReadOnlyList<Exception> Failures
            {
                set
                {
                    _failures.Clear();
                    foreach (Exception failure in value)
                    {
                        _failures.Enqueue(failure);
                    }
                }
            }

            public SyncPairSettings? LastSyncPair { get; private set; }

            public SyncRunRequest? LastRequest { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public int RunCount { get; private set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                LastSyncPair = syncPair;
                if (_failures.Count > 0)
                {
                    throw _failures.Dequeue();
                }

                if (Failure is not null)
                {
                    throw Failure;
                }

                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                Requests.Add(request);
                return RunOnceAsync(syncPair, cancellationToken);
            }
        }

        private class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private class ReleasingLockedFileSyncPairWork : ISyncPairWork
        {
            private readonly Action _releaseLock;
            private readonly LocalFileScanner _scanner = new();

            public ReleasingLockedFileSyncPairWork(Action releaseLock)
            {
                _releaseLock = releaseLock;
            }

            public int RunCount { get; private set; }

            public IReadOnlyList<string> ScannedPaths { get; private set; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                try
                {
                    IReadOnlyList<LocalFileSnapshot> files = await _scanner
                        .ScanAsync(syncPair.LocalRootPath, cancellationToken)
                        .ConfigureAwait(false);
                    ScannedPaths = files.Select(file => file.RelativePath).ToList();
                }
                catch (LocalFileUnavailableException) when (RunCount == 1)
                {
                    _releaseLock();
                    throw;
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, cancellationToken);
            }
        }

        private class RestoringMissingRootSyncPairWork : ISyncPairWork
        {
            private readonly string _root;
            private readonly Action _restoreRoot;
            private readonly LocalFileScanner _scanner = new();

            public RestoringMissingRootSyncPairWork(string root, Action restoreRoot)
            {
                _root = root;
                _restoreRoot = restoreRoot;
            }

            public int RunCount { get; private set; }

            public IReadOnlyList<string> ScannedPaths { get; private set; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                try
                {
                    IReadOnlyList<LocalFileSnapshot> files = await _scanner
                        .ScanAsync(_root, cancellationToken)
                        .ConfigureAwait(false);
                    ScannedPaths = files.Select(file => file.RelativePath).ToList();
                }
                catch (DirectoryNotFoundException) when (RunCount == 1)
                {
                    _restoreRoot();
                    throw;
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, cancellationToken);
            }
        }

        private class CancellationObservingSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _cancellationObserved = CreateCompletionSource();
            private readonly TaskCompletionSource _runStarted = CreateCompletionSource();

            public int RunCount { get; private set; }

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                _runStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, cancellationToken);
            }

            public async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
            {
                try
                {
                    await _cancellationObserved.Task.WaitAsync(timeout).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                return _runStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class CancellationSideEffectSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _cancellationObserved = CreateCompletionSource();
            private readonly TaskCompletionSource _runStarted = CreateCompletionSource();
            private readonly Exception _sideEffect;

            public CancellationSideEffectSyncPairWork(Exception sideEffect)
            {
                _sideEffect = sideEffect;
            }

            public int RunCount { get; private set; }

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                _runStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw _sideEffect;
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, cancellationToken);
            }

            public async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
            {
                try
                {
                    await _cancellationObserved.Task.WaitAsync(timeout).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                return _runStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class BlockingFirstRunSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _runStarted = CreateCompletionSource();
            private readonly TaskCompletionSource _releaseRun = CreateCompletionSource();

            public int RunCount { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                _runStarted.TrySetResult();
                if (RunCount == 1)
                {
                    await _releaseRun.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return RunOnceAsync(syncPair, cancellationToken);
            }

            public void ReleaseRun()
            {
                _releaseRun.TrySetResult();
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                return _runStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class BlockingSyncPairWork : ISyncPairWork
        {
            private readonly object _gate = new();
            private TaskCompletionSource _currentRunStarted = CreateCompletionSource();
            private TaskCompletionSource _currentRunRelease = CreateCompletionSource();
            private TaskCompletionSource _secondRunStarted = CreateCompletionSource();

            public int RunCount { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                await RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                TaskCompletionSource release;
                lock (_gate)
                {
                    RunCount++;
                    Requests.Add(request);
                    release = _currentRunRelease;
                    _currentRunStarted.TrySetResult();
                    if (RunCount >= 2)
                    {
                        _secondRunStarted.TrySetResult();
                    }
                }

                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            public void ReleaseCurrentRun()
            {
                lock (_gate)
                {
                    _currentRunRelease.TrySetResult();
                    _currentRunStarted = CreateCompletionSource();
                    _currentRunRelease = CreateCompletionSource();
                }
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                Task task;
                lock (_gate)
                {
                    task = _currentRunStarted.Task;
                }

                return task.WaitAsync(timeout);
            }

            public async Task WaitForRunCountAsync(int runCount, TimeSpan timeout)
            {
                if (RunCount >= runCount)
                {
                    return;
                }

                await _secondRunStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class BlockingFirstFailureSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _firstRunStarted = CreateCompletionSource();
            private readonly TaskCompletionSource _releaseFirstRun = CreateCompletionSource();

            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (Requests.Count != 1)
                {
                    return;
                }

                _firstRunStarted.TrySetResult();
                await _releaseFirstRun.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException("network down");
            }

            public void ReleaseFirstRun()
            {
                _releaseFirstRun.TrySetResult();
            }

            public Task WaitForFirstRunAsync(TimeSpan timeout)
            {
                return _firstRunStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class TestIOException : IOException
        {
            public TestIOException(int hresult)
                : base("Synthetic I/O failure.")
            {
                HResult = hresult;
            }
        }
    }
}
