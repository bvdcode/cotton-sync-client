// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncEnginePairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_ForwardsRemoteDeleteEvidenceThroughLocalRetries(bool deferred)
        {
            RemoteChangeFeedSnapshot changes = RemoteChangeFeedSnapshot.FromChanges(
            [
                new SyncChangeDto
                {
                    Id = 1, Kind = SyncChangeKind.FolderDeleted, Name = "Archive",
                    ItemId = Guid.NewGuid(), ParentNodeId = Guid.NewGuid(), LayoutId = Guid.NewGuid(),
                },
            ]);
            FakeSyncEngine engine = new();
            if (deferred)
            {
                SyncRunResult pending = new();
                pending.RecordDeferredLocalPath("Archive/modified.txt");
                engine.ResultsToReturn.Enqueue(pending);
                engine.ResultsToReturn.Enqueue(new SyncRunResult());
            }
            SyncEnginePairWork work = new(engine, delayAsync: (_, _) => Task.CompletedTask);

            await work.RunOnceAsync(CreateSyncPair(Guid.NewGuid()), SyncRunRequest.Full.WithRemoteChanges(changes));

            Assert.Multiple(() =>
            {
                Assert.That(engine.OptionsHistory, Has.Count.EqualTo(deferred ? 2 : 1));
                Assert.That(engine.OptionsHistory.Select(options => options?.RemoteChanges), Is.All.SameAs(changes));
            });
        }
    }
}
