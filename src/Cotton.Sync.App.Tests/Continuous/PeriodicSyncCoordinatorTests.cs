// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sdk;

namespace Cotton.Sync.App.Tests.Continuous
{
    public class PeriodicSyncCoordinatorTests
    {
        [Test]
        public async Task StartAsync_RequestsImmediateSyncAllByDefault()
        {
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new PeriodicSyncCoordinator(supervisor, TimeSpan.FromMinutes(1));

            await coordinator.StartAsync();
            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncAllCallCount, Is.EqualTo(1));
                Assert.That(supervisor.LastSyncAllRequest?.Causes, Is.EqualTo(SyncRunCause.Periodic));
            });
        }

        [Test]
        public async Task PeriodicTick_RequestsSyncAll()
        {
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new PeriodicSyncCoordinator(
                supervisor,
                TimeSpan.FromMilliseconds(25),
                runImmediately: false);

            await coordinator.StartAsync();
            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncAllCallCount, Is.GreaterThanOrEqualTo(1));
            });
        }

        [Test]
        public async Task StopAsync_CancelsPeriodicRequests()
        {
            var supervisor = new FakeSyncSupervisor();
            var coordinator = new PeriodicSyncCoordinator(
                supervisor,
                TimeSpan.FromMilliseconds(100),
                runImmediately: false);

            await coordinator.StartAsync();
            await coordinator.StopAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            Assert.That(supervisor.SyncAllCallCount, Is.Zero);
        }

        [Test]
        public async Task TransientServerFailure_RetriesBeforeNormalPeriodicInterval()
        {
            var supervisor = new FakeSyncSupervisor();
            supervisor.SyncAllFailures.Enqueue(new AggregateException(
                new CottonApiException(
                    System.Net.HttpStatusCode.BadGateway,
                    "502 Bad Gateway",
                    "Cotton API request failed with status 502.")));
            var coordinator = new PeriodicSyncCoordinator(
                supervisor,
                interval: TimeSpan.FromMinutes(10),
                connectionRetryInterval: TimeSpan.FromMilliseconds(20));

            await coordinator.StartAsync();
            bool retried = await supervisor.WaitForSecondSyncAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(retried, Is.True);
                Assert.That(supervisor.SyncAllCallCount, Is.EqualTo(2));
                Assert.That(supervisor.LastSyncAllRequest?.Causes, Is.EqualTo(SyncRunCause.Periodic));
            });
        }

        private class FakeSyncSupervisor : ISyncSupervisor
        {
            private readonly TaskCompletionSource _syncRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _secondSyncRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public int SyncAllCallCount { get; private set; }

            public SyncRunRequest? LastSyncAllRequest { get; private set; }

            public Queue<Exception> SyncAllFailures { get; } = [];

            public Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(bool startPaused, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncAllCallCount++;
                _syncRequested.TrySetResult();
                if (SyncAllCallCount >= 2)
                {
                    _secondSyncRequested.TrySetResult();
                }

                if (SyncAllFailures.TryDequeue(out Exception? failure))
                {
                    throw failure;
                }

                return Task.CompletedTask;
            }

            public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                LastSyncAllRequest = request;
                return SyncAllAsync(cancellationToken);
            }

            public Task SyncNowAsync(
                Guid syncPairId,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return SyncNowAsync(syncPairId, cancellationToken);
            }

            public async Task<bool> WaitForSyncAsync(TimeSpan timeout)
            {
                Task completed = await Task.WhenAny(_syncRequested.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == _syncRequested.Task;
            }

            public async Task<bool> WaitForSecondSyncAsync(TimeSpan timeout)
            {
                Task completed = await Task.WhenAny(_secondSyncRequested.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == _secondSyncRequested.Task;
            }
        }
    }
}
