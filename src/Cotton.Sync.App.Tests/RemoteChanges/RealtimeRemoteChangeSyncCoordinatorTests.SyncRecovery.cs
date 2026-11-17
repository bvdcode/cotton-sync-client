// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using System.Net;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Status;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.RemoteChanges
{
    public partial class RealtimeRemoteChangeSyncCoordinatorTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RemoteChanges_RetryOnlyFailedPairWithoutAnotherEvent(bool remoteVersionChanged)
        {
            Guid transientPair = Guid.NewGuid();
            Guid deniedPair = Guid.NewGuid();
            Guid healthyPair = Guid.NewGuid();
            ConcurrentDictionary<Guid, int> attempts = new();
            TaskCompletionSource recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeSyncSupervisor supervisor = new()
            {
                CurrentStatuses = new[] { transientPair, deniedPair, healthyPair }
                    .Select(id => new SyncPairStatus(id, "Test", SyncPairRunState.Idle, null, null, DateTime.UtcNow)).ToArray(),
                OnSyncPair = (id, _) =>
                {
                    int attempt = attempts.AddOrUpdate(id, 1, (_, count) => count + 1);
                    if (id == deniedPair)
                    {
                        return Task.FromException(new HttpRequestException("Denied", null, HttpStatusCode.Forbidden));
                    }
                    if (id == transientPair)
                    {
                        if (attempt < 3)
                        {
                            Exception failure = remoteVersionChanged
                                ? new RemoteFileVersionChangedException("file.bin", new HttpRequestException())
                                : new HttpRequestException("Connection interrupted.");
                            return Task.FromException(failure);
                        }
                        recovered.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
            };
            RealtimeRemoteChangeSyncCoordinator coordinator = new(
                new FakeCottonRealtimeClient(), supervisor, new RealtimeTestAuthFlow(), DebounceInterval,
                connectionRetryInterval: TimeSpan.FromMilliseconds(10));
            try
            {
                await coordinator.StartAsync();
                await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Multiple(() =>
                {
                    Assert.That(attempts[transientPair], Is.EqualTo(3));
                    Assert.That(attempts[deniedPair], Is.EqualTo(1));
                    Assert.That(attempts[healthyPair], Is.EqualTo(1));
                });
            }
            finally
            {
                await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        [Test]
        public async Task StopAsync_CancelsRemoteSyncRetryDelay()
        {
            TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            FakeSyncSupervisor supervisor = new()
            {
                OnSyncPair = (_, _) =>
                {
                    Interlocked.Increment(ref count);
                    attempted.TrySetResult();
                    return Task.FromException(new HttpRequestException("Connection interrupted."));
                },
            };
            RealtimeRemoteChangeSyncCoordinator coordinator = new(
                new FakeCottonRealtimeClient(), supervisor, new RealtimeTestAuthFlow(), DebounceInterval,
                connectionRetryInterval: TimeSpan.FromHours(1));
            await coordinator.StartAsync();
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(count, Is.EqualTo(1));
            Assert.That(coordinator.PendingRequestCount, Is.Zero);
        }
    }
}
