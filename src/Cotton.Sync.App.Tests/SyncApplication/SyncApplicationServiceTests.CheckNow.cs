// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task UserRefresh_RequestsFeedCheck(bool allPairs)
        {
            FakeSyncSupervisor supervisor = new();
            SyncApplicationService service = CreateService(new InMemorySyncPairSettingsStore(), supervisor: supervisor);
            if (allPairs)
            {
                await service.SyncAllAsync();
            }
            else
            {
                await service.SyncNowAsync(Guid.NewGuid());
            }

            Assert.That(supervisor.LastSyncRequest?.Causes, Is.EqualTo(SyncRunCause.CheckNow));
        }

        [Test]
        public async Task ExplicitFullReconcile_PreservesRequestedCause()
        {
            FakeSyncSupervisor supervisor = new();
            SyncApplicationService service = CreateService(new InMemorySyncPairSettingsStore(), supervisor: supervisor);

            await service.SyncNowAsync(Guid.NewGuid(), SyncRunRequest.Full);

            Assert.That(supervisor.LastSyncRequest?.Causes, Is.EqualTo(SyncRunCause.Manual));
        }
    }
}
