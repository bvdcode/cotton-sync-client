// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public partial class LocalChangeSyncCoordinatorTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task TransientSyncFailure_RetriesScopedRequestUntilItSucceeds(bool remoteListingChanges)
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            FakeWatcherFactory watcherFactory = new();
            FakeSyncSupervisor supervisor = new();
            supervisor.SyncNowExceptions.Enqueue(remoteListingChanges
                ? new RemoteDirectoryListingUnavailableException("Remote listing changed.")
                : new HttpRequestException("Network unavailable."));
            RecordingLogger<LocalChangeSyncCoordinator> logger = new();
            LocalChangeSyncCoordinator coordinator = new(
                new FakeSyncPairSettingsStore([syncPair]),
                supervisor,
                watcherFactory,
                TimeSpan.Zero,
                logger,
                connectionRetryInterval: TimeSpan.FromMilliseconds(1),
                delayAsync: static (_, _) => Task.CompletedTask);
            await coordinator.StartAsync();

            watcherFactory.CreatedWatchers[syncPair.Id].Raise(FullPath(syncPair, "offline-edit.txt"));

            bool retried = await supervisor.WaitForSyncCallCountAsync(2, TimeSpan.FromSeconds(2));
            await coordinator.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(retried, Is.True);
                Assert.That(supervisor.SyncNowCallCount, Is.EqualTo(2));
                Assert.That(supervisor.Requests, Has.Count.EqualTo(2));
                Assert.That(
                    supervisor.Requests.Select(static request => request.LocalChangedPaths),
                    Is.All.EqualTo(new[] { "offline-edit.txt" }));
                Assert.That(coordinator.PendingRequestCount, Is.Zero);
                Assert.That(
                    logger.Entries.Any(entry => entry.Level == LogLevel.Warning
                        && entry.Message.Contains("retrying after", StringComparison.Ordinal)),
                    Is.True);
                Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Error), Is.False);
            });
        }

    }
}
