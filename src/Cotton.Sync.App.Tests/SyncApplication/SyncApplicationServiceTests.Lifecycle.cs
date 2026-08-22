// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        [Test]
        public async Task StartSyncAsync_StartsSupervisorAndLocalChanges()
        {
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);

            await service.StartSyncAsync();

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.StartCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StartCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StartCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task StartSyncAsync_StartsLifecycleComponentsBeforeSyncRunners()
        {
            List<string> calls = [];
            FakeSyncCoreLifecycleComponent lifecycle = new FakeSyncCoreLifecycleComponent("cloud-files", calls);
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator(calls);
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator(calls);
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator(calls);
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync,
                syncCoreLifecycleComponents: [lifecycle]);

            await service.StartSyncAsync();

            Assert.That(calls, Is.EqualTo(new[]
            {
                "cloud-files:start",
                "supervisor:start",
                "local:start",
                "remote:start",
                "periodic:start",
            }));
        }

        [Test]
        public void StartSyncAsync_RollsBackLifecycleComponentsWhenStartupFails()
        {
            List<string> calls = [];
            InvalidOperationException startupError = new InvalidOperationException("Cloud Files connect failed.");
            FakeSyncCoreLifecycleComponent lifecycle = new FakeSyncCoreLifecycleComponent("cloud-files", calls)
            {
                StartException = startupError,
            };
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor,
                syncCoreLifecycleComponents: [lifecycle]);

            InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartSyncAsync())!;

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.SameAs(startupError));
                Assert.That(supervisor.StartCallCount, Is.Zero);
                Assert.That(calls, Is.EqualTo(new[] { "cloud-files:start" }));
            });
        }

        [Test]
        public void StartSyncAsync_RollsBackStartedComponentsWhenRemoteStartupFails()
        {
            List<string> calls = [];
            InvalidOperationException startupError = new InvalidOperationException("Remote listener failed.");
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator(calls);
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator(calls)
            {
                StartException = startupError,
            };
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator(calls);
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);

            InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartSyncAsync())!;

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.SameAs(startupError));
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StopCallCount, Is.Zero);
                Assert.That(periodicSync.StartCallCount, Is.Zero);
                Assert.That(periodicSync.StopCallCount, Is.Zero);
                Assert.That(calls, Is.EqualTo(new[]
                {
                    "supervisor:start",
                    "local:start",
                    "remote:start",
                    "local:stop",
                    "supervisor:stop",
                }));
            });
        }

        [Test]
        public void StartSyncAsync_RollsBackStartedComponentsWhenPeriodicStartupFails()
        {
            List<string> calls = [];
            InvalidOperationException startupError = new InvalidOperationException("Periodic sync failed.");
            FakeAuthFlow authFlow = new FakeAuthFlow();
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator(calls);
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator(calls);
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator(calls)
            {
                StartException = startupError,
            };
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                authFlow: authFlow,
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);

            InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartSyncAsync())!;

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.SameAs(startupError));
                Assert.That(authFlow.RestoreSessionCallCount, Is.Zero);
                Assert.That(remoteChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StopCallCount, Is.Zero);
                Assert.That(calls, Is.EqualTo(new[]
                {
                    "supervisor:start",
                    "local:start",
                    "remote:start",
                    "periodic:start",
                    "remote:stop",
                    "local:stop",
                    "supervisor:stop",
                }));
            });
        }

        [Test]
        public async Task StopSyncAsync_StopsLocalChangesAndSupervisor()
        {
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor();
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator();
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator();
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator();
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync);

            await service.StopSyncAsync();

            Assert.Multiple(() =>
            {
                Assert.That(localChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(remoteChanges.StopCallCount, Is.EqualTo(1));
                Assert.That(periodicSync.StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.StopCallCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task StopSyncAsync_StopsLifecycleComponentsAfterSyncRunners()
        {
            List<string> calls = [];
            FakeSyncCoreLifecycleComponent lifecycle = new FakeSyncCoreLifecycleComponent("cloud-files", calls);
            FakeSyncSupervisor supervisor = new FakeSyncSupervisor(calls);
            FakeLocalChangeSyncCoordinator localChanges = new FakeLocalChangeSyncCoordinator(calls);
            FakeRemoteChangeSyncCoordinator remoteChanges = new FakeRemoteChangeSyncCoordinator(calls);
            FakePeriodicSyncCoordinator periodicSync = new FakePeriodicSyncCoordinator(calls);
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                supervisor: supervisor,
                localChanges: localChanges,
                remoteChanges: remoteChanges,
                periodicSync: periodicSync,
                syncCoreLifecycleComponents: [lifecycle]);

            await service.StartSyncAsync();
            calls.Clear();

            await service.StopSyncAsync();

            Assert.That(calls, Is.EqualTo(new[]
            {
                "remote:stop",
                "periodic:stop",
                "local:stop",
                "supervisor:stop",
                "cloud-files:stop",
            }));
        }

    }
}
