// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.RemoteChanges;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    public partial class RealtimeRemoteChangeSyncCoordinatorTests
    {
        [Test]
        public async Task StopAsync_CancelsClosedConnectionRetryAndIgnoresLateEvents()
        {
            FakeCottonRealtimeClient realtime = new();
            RealtimeTestAuthFlow auth = new();
            FakeSyncSupervisor supervisor = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(realtime, supervisor, auth,
                DebounceInterval, connectionRetryInterval: TimeSpan.FromMinutes(1));
            await coordinator.StartAsync();
            Assert.That(await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2)), Is.True);
            realtime.RaiseClosed(new IOException("Connection lost"));

            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            realtime.RaiseClosed(null);
            realtime.RaiseReconnected();
            await Task.Delay(60);

            Assert.Multiple(() =>
            {
                Assert.That(auth.RestoreCalls, Is.EqualTo(1));
                Assert.That(realtime.StartCallCount, Is.EqualTo(1));
                Assert.That(realtime.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.SyncAllCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task StopAsync_CancelsAuthenticationAfterClosedConnection()
        {
            TaskCompletionSource restoring = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RealtimeTestAuthFlow auth = new();
            auth.RestoreAction = async cancellationToken =>
            {
                if (auth.RestoreCalls > 1)
                {
                    restoring.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            };
            FakeCottonRealtimeClient realtime = new();
            FakeSyncSupervisor supervisor = new();
            RealtimeRemoteChangeSyncCoordinator coordinator = new(realtime, supervisor, auth,
                DebounceInterval, connectionRetryInterval: TimeSpan.FromMilliseconds(20));
            try
            {
                await coordinator.StartAsync();
                Assert.That(await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2)), Is.True);
                realtime.RaiseClosed(null);
                await restoring.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.That(realtime.StartCallCount, Is.EqualTo(1));
        }
    }
}
