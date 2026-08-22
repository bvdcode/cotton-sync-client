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
        public async Task StopAsync_ClearsQueuedSyncRequest()
        {
            BlockingFirstRunSyncPairWork work = new BlockingFirstRunSyncPairWork();
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
            CancellationObservingSyncPairWork work = new CancellationObservingSyncPairWork();
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
            CancellationSideEffectSyncPairWork work = new CancellationSideEffectSyncPairWork(new IOException("Transport was canceled."));
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
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
            BlockingFirstRunSyncPairWork work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            using CancellationTokenSource cancellation = new CancellationTokenSource();

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
            BlockingFirstRunSyncPairWork work = new BlockingFirstRunSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            using CancellationTokenSource cancellation = new CancellationTokenSource();

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
    }
}
