// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    public partial class RealtimeRemoteChangeSyncCoordinatorTests
    {
        [Test]
        public async Task StartAsync_RefreshesSessionBeforeConnectingAndChecksMissedChanges()
        {
            FakeCottonRealtimeClient realtime = new()
            {
                StartException = new HttpRequestException("Expired access token", null, HttpStatusCode.Unauthorized),
            };
            RealtimeTestAuthFlow auth = new()
            {
                RestoreAction = _ =>
                {
                    realtime.StartException = null;
                    return Task.CompletedTask;
                },
            };
            FakeSyncSupervisor supervisor = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(realtime, supervisor, auth, DebounceInterval);
            try
            {
                await coordinator.StartAsync();
                Assert.That(await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2)), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(auth.RestoreCalls, Is.EqualTo(1));
                    Assert.That(realtime.StartCallCount, Is.EqualTo(1));
                    Assert.That(supervisor.LastSyncAllRequest?.Causes, Is.EqualTo(SyncRunCause.RealtimeRemoteChange));
                });
            }
            finally
            {
                await coordinator.StopAsync();
            }
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public async Task StartAsync_RetriesEventConnectionWithoutFailingSyncStartup(HttpStatusCode statusCode)
        {
            FakeCottonRealtimeClient realtime = new()
            {
                StartException = new HttpRequestException("Event connection failed", null, statusCode),
            };
            FakeSyncSupervisor supervisor = new();
            RealtimeTestAuthFlow auth = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(
                realtime, supervisor, auth, DebounceInterval,
                connectionRetryInterval: TimeSpan.FromMilliseconds(20));
            try
            {
                await coordinator.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(realtime.StartCallCount, Is.GreaterThanOrEqualTo(1));
                Assert.That(realtime.StopCallCount, Is.Zero);
                realtime.StartException = null;
                Assert.That(await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(auth.RestoreCalls, Is.GreaterThanOrEqualTo(2));
            }
            finally
            {
                await coordinator.StopAsync();
            }

            int stoppedAttempts = realtime.StartCallCount;
            realtime.RaiseRemoteFileTreeChanged("FileCreated");
            await Task.Delay(60);
            Assert.Multiple(() =>
            {
                Assert.That(realtime.StartCallCount, Is.EqualTo(stoppedAttempts));
                Assert.That(realtime.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.SyncAllCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task StopAsync_CancelsPendingAuthenticationAndDoesNotStartConnection()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RealtimeTestAuthFlow auth = new()
            {
                RestoreAction = async cancellationToken =>
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
            };
            FakeCottonRealtimeClient realtime = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(realtime, new FakeSyncSupervisor(), auth);
            await coordinator.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(realtime.StartCallCount, Is.Zero);
        }

        [Test]
        public async Task ConnectionAttemptTimeout_RetriesWithFreshSession()
        {
            RealtimeTestAuthFlow auth = new();
            auth.RestoreAction = async cancellationToken =>
            {
                if (auth.RestoreCalls == 1)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            };
            FakeCottonRealtimeClient realtime = new();
            FakeSyncSupervisor supervisor = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(
                realtime, supervisor, auth, DebounceInterval,
                connectionAttemptTimeout: TimeSpan.FromMilliseconds(20),
                connectionRetryInterval: TimeSpan.FromMilliseconds(20));
            try
            {
                await coordinator.StartAsync();
                Assert.That(await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(auth.RestoreCalls, Is.EqualTo(2));
            }
            finally
            {
                await coordinator.StopAsync();
            }
        }
    }
}
