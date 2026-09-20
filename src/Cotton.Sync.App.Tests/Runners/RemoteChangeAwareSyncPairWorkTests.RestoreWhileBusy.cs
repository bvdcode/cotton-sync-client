// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        [Test]
        public void CheckNow_AfterActionRequired_RetainsExplicitRecovery()
        {
            SyncPairRequestQueue queue = new(isBlocked: false);
            queue.TryStart(SyncRunRequest.ForFull(SyncRunCause.Periodic));
            queue.FinishAfterFailure(new SyncActionRequiredException("Remote paths need reconciliation."));

            Assert.That(queue.TryStart(SyncRunRequest.ForFull(SyncRunCause.CheckNow)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(queue.GetActiveRequest().IsFull, Is.True);
                Assert.That(queue.GetActiveRequest().Causes.HasFlag(SyncRunCause.Manual), Is.True);
            });
        }

        [Test]
        public async Task CheckNow_RestoresOnlyChangedFilesWithoutFullScan()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            SyncChangeDto[] changes = CreateFileChanges(11, 7, pair.RemoteRootNodeId).ToArray();
            foreach (SyncChangeDto change in changes)
            {
                change.Kind = SyncChangeKind.FileRestored;
            }
            RemoteChangeFeedBatch batch = new(pair.Id.ToString("D"), 10, 17, false, false, 5, changes);
            FakeRemoteChangeFeedReader feed = new(batch);
            RemoteChangeAwareSyncPairWork work = new(inner, feed, stateStore);

            await work.RunOnceAsync(pair, SyncRunRequest.ForFull(SyncRunCause.CheckNow));

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(1));
                Assert.That(inner.LastRequest!.IsFull, Is.False);
                Assert.That(inner.LastRequest.LocalChangedPaths, Has.Count.EqualTo(7));
                Assert.That(feed.AcknowledgedBatches, Is.EqualTo(new[] { batch }));
            });
        }

        [Test]
        public async Task RestoreEventsDuringActivePass_AreReadByTheQueuedPass()
        {
            SyncPairSettings pair = CreateSyncPair(SyncPairMode.WindowsVirtualFiles);
            FakeSyncPairWork inner = new();
            FakeSyncStateStore stateStore = new();
            RemoteChangeFeedBatch first = RestoreBatch(11, 4);
            RemoteChangeFeedBatch second = RestoreBatch(15, 7);
            FakeRemoteChangeFeedReader feed = new(first, second);
            RemoteChangeAwareSyncPairWork work = new(inner, feed, stateStore);
            SyncPairRunner runner = new(pair, work);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            inner.OnRunAsync = async (count, _) =>
            {
                if (count == 1)
                {
                    started.TrySetResult();
                    await release.Task;
                }
            };
            Task active = runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int index = 0; index < 7; index++)
                {
                    await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));
                }
                Assert.That(feed.AcknowledgedBatches, Is.Empty);
            }
            finally
            {
                release.TrySetResult();
                await active.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Multiple(() =>
            {
                Assert.That(inner.RunCallCount, Is.EqualTo(2));
                Assert.That(inner.LastRequest!.IsFull, Is.False);
                Assert.That(inner.LastRequest.LocalChangedPaths, Has.Count.EqualTo(7));
                Assert.That(feed.AcknowledgedBatches, Is.EqualTo(new[] { first, second }));
            });

            RemoteChangeFeedBatch RestoreBatch(long cursor, int count)
            {
                SyncChangeDto[] changes = CreateFileChanges(cursor, count, pair.RemoteRootNodeId).ToArray();
                foreach (SyncChangeDto change in changes)
                {
                    change.Kind = SyncChangeKind.FileRestored;
                }
                return new RemoteChangeFeedBatch(pair.Id.ToString("D"), cursor - 1, cursor + count - 1,
                    false, false, 5, changes);
            }
        }
    }
}
