// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncEnginePairWorkTests
    {
        [Test]
        public async Task RunOnceAsync_UsesStreamingFastPathForInitialPopulationWithExpiredCursor()
        {
            FakeSyncEngine engine = new FakeSyncEngine();
            SyncEnginePairWork work = new SyncEnginePairWork(engine);
            SyncPairSettings syncPair = CreateSyncPair(Guid.NewGuid());
            syncPair.Mode = SyncPairMode.WindowsVirtualFiles;

            await work.RunOnceAsync(
                syncPair,
                SyncRunRequest.ForFull(
                    SyncRunCause.InitialPopulation | SyncRunCause.RemoteCursorExpired));

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.True);
                Assert.That(engine.LastOptions.AllowInitialVirtualFilesStreaming, Is.True);
                Assert.That(engine.LastOptions.RestoreMissingRemoteOnlyPlaceholders, Is.True);
            });
        }
    }
}
