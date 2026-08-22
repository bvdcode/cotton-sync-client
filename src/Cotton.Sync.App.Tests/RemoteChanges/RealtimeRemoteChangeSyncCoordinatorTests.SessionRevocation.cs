// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sdk.Realtime;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    public partial class RealtimeRemoteChangeSyncCoordinatorTests
    {
        [Test]
        public async Task SessionRevoked_InvokesHandlerAndStopsRealtime()
        {
            FakeCottonRealtimeClient realtime = new FakeCottonRealtimeClient();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeSessionRevocationHandler sessionRevocationHandler = new FakeSessionRevocationHandler();
            RealtimeRemoteChangeSyncCoordinator coordinator = new RealtimeRemoteChangeSyncCoordinator(
                realtime,
                supervisor,
                DebounceInterval,
                sessionRevocationHandler);
            await coordinator.StartAsync();

            realtime.RaiseSessionRevoked();

            bool handled = await sessionRevocationHandler.WaitForCallAsync(TimeSpan.FromSeconds(2));
            bool stopped = await realtime.WaitForStopAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(stopped, Is.True);
                Assert.That(sessionRevocationHandler.CallCount, Is.EqualTo(1));
                Assert.That(supervisor.SyncAllCallCount, Is.Zero);
                Assert.That(realtime.StopCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SessionRevoked_CancelsPendingRemoteSyncRequest()
        {
            FakeCottonRealtimeClient realtime = new FakeCottonRealtimeClient();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeSessionRevocationHandler sessionRevocationHandler = new FakeSessionRevocationHandler();
            RealtimeRemoteChangeSyncCoordinator coordinator = new RealtimeRemoteChangeSyncCoordinator(
                realtime,
                supervisor,
                TimeSpan.FromMilliseconds(100),
                sessionRevocationHandler);
            await coordinator.StartAsync();

            realtime.RaiseRemoteFileTreeChanged("FileUpdated");
            realtime.RaiseSessionRevoked();

            bool handled = await sessionRevocationHandler.WaitForCallAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(supervisor.SyncAllCallCount, Is.Zero);
            });
        }

        [Test]
        public async Task SessionRevoked_CancelsRunningRemoteSyncRequestBeforeHandlerCompletes()
        {
            FakeCottonRealtimeClient realtime = new FakeCottonRealtimeClient();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor
            {
                BlockSyncAll = true,
            };
            FakeSessionRevocationHandler sessionRevocationHandler = new FakeSessionRevocationHandler
            {
                BlockHandler = true,
            };
            RealtimeRemoteChangeSyncCoordinator coordinator = new RealtimeRemoteChangeSyncCoordinator(
                realtime,
                supervisor,
                TimeSpan.Zero,
                sessionRevocationHandler);
            await coordinator.StartAsync();

            realtime.RaiseRemoteFileTreeChanged("FileUpdated");
            bool observed = await supervisor.WaitForSyncAsync(TimeSpan.FromSeconds(2));
            realtime.RaiseSessionRevoked();
            bool handled = await sessionRevocationHandler.WaitForCallAsync(TimeSpan.FromSeconds(2));
            bool canceled = await supervisor.WaitForSyncCancellationAsync(TimeSpan.FromSeconds(2));

            sessionRevocationHandler.ReleaseHandler();
            bool stopped = await realtime.WaitForStopAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.True);
                Assert.That(handled, Is.True);
                Assert.That(canceled, Is.True);
                Assert.That(stopped, Is.True);
                Assert.That(supervisor.SyncAllCallCount, Is.EqualTo(1));
                Assert.That(sessionRevocationHandler.CallCount, Is.EqualTo(1));
            });
        }

    }
}
