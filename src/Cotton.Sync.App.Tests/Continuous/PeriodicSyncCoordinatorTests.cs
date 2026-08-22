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
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            PeriodicSyncCoordinator coordinator = new PeriodicSyncCoordinator(supervisor, TimeSpan.FromMinutes(1));

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
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            PeriodicSyncCoordinator coordinator = new PeriodicSyncCoordinator(
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
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            PeriodicSyncCoordinator coordinator = new PeriodicSyncCoordinator(
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
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            supervisor.SyncAllFailures.Enqueue(new AggregateException(
                new CottonApiException(
                    System.Net.HttpStatusCode.BadGateway,
                    "502 Bad Gateway",
                    "Cotton API request failed with status 502.")));
            PeriodicSyncCoordinator coordinator = new PeriodicSyncCoordinator(
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

        [Test]
        public async Task SafetyReconcileInterval_AddsInternalMaintenanceToPeriodicRequest()
        {
            MutableTimeProvider timeProvider = new();
            FakeSyncSupervisor supervisor = new();
            PeriodicSyncCoordinator coordinator = new(
                supervisor,
                interval: TimeSpan.FromMinutes(10),
                runImmediately: false,
                delayAsync: CreateAdvancingDelay(timeProvider),
                safetyReconcileInterval: TimeSpan.FromMinutes(20),
                timeProvider: timeProvider);

            await coordinator.StartAsync();
            bool observed = await supervisor.WaitForSyncCountAsync(2, TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncAllRequests, Has.Count.GreaterThanOrEqualTo(2));
                Assert.That(supervisor.SyncAllRequests[0].Causes, Is.EqualTo(SyncRunCause.Periodic));
                Assert.That(
                    supervisor.SyncAllRequests[1].Causes,
                    Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.InternalMaintenance));
            });
        }

        [Test]
        public async Task FailedSafetyReconcile_DoesNotRetryFullWorkOnNextPeriodicTick()
        {
            MutableTimeProvider timeProvider = new();
            FakeSyncSupervisor supervisor = new()
            {
                FailureCallNumber = 2,
            };
            PeriodicSyncCoordinator coordinator = new(
                supervisor,
                interval: TimeSpan.FromMinutes(10),
                runImmediately: false,
                delayAsync: CreateAdvancingDelay(timeProvider),
                safetyReconcileInterval: TimeSpan.FromMinutes(20),
                timeProvider: timeProvider);

            await coordinator.StartAsync();
            bool observed = await supervisor.WaitForSyncCountAsync(3, TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(supervisor.SyncAllRequests, Has.Count.GreaterThanOrEqualTo(3));
                Assert.That(
                    supervisor.SyncAllRequests[1].Causes,
                    Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.InternalMaintenance));
                Assert.That(supervisor.SyncAllRequests[2].Causes, Is.EqualTo(SyncRunCause.Periodic));
            });
        }

        private static Func<TimeSpan, CancellationToken, Task> CreateAdvancingDelay(
            MutableTimeProvider timeProvider)
        {
            return async (delay, cancellationToken) =>
            {
                timeProvider.Advance(delay);
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            };
        }

        private class FakeSyncSupervisor : ISyncSupervisor
        {
            private readonly TaskCompletionSource _syncRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _secondSyncRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public int SyncAllCallCount { get; private set; }

            public SyncRunRequest? LastSyncAllRequest { get; private set; }

            public Queue<Exception> SyncAllFailures { get; } = [];

            public int? FailureCallNumber { get; init; }

            public List<SyncRunRequest> SyncAllRequests { get; } = [];

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

                if (FailureCallNumber == SyncAllCallCount)
                {
                    throw new InvalidOperationException("Non-transient periodic sync failure.");
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
                SyncAllRequests.Add(request);
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

            public async Task<bool> WaitForSyncCountAsync(int expectedCount, TimeSpan timeout)
            {
                DateTime deadline = DateTime.UtcNow.Add(timeout);
                while (SyncAllCallCount < expectedCount && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
                }

                return SyncAllCallCount >= expectedCount;
            }
        }

        private class MutableTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow()
            {
                return _utcNow;
            }

            public void Advance(TimeSpan duration)
            {
                _utcNow = _utcNow.Add(duration);
            }
        }
    }
}
