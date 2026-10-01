// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.Tests.Runners
{
    public class SyncPairRequestQueueTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SupersedeRemoteCheck_KeepsCancelledRequestAndLocalEvidence(bool cancellationAssignedFirst)
        {
            SyncPairRequestQueue queue = new(isBlocked: false);
            SyncRunRequest remote = SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange);
            LocalPathRename rename = new("original.txt", "renamed.txt");
            SyncRunRequest local = SyncRunRequest.ForLocalChangedPaths(
                ["downloaded.txt", "original.txt", "renamed.txt", "deleted.txt"], ["deleted.txt"],
                localRenames: [rename]);
            using CancellationTokenSource cancellation = new();
            Assert.That(queue.TryStart(remote), Is.True);
            if (cancellationAssignedFirst)
            {
                queue.SetActiveCancellation(cancellation);
            }
            Assert.That(queue.TryStart(local), Is.False);
            if (!cancellationAssignedFirst)
            {
                queue.SetActiveCancellation(cancellation);
            }
            queue.RequeueSupersededRequest();
            queue.ClearActiveCancellation(cancellation);
            Assert.That(queue.CompletePassOrTakeQueued(), Is.True);
            SyncRunRequest pending = queue.GetActiveRequest();
            Assert.Multiple(() =>
            {
                Assert.That(cancellation.IsCancellationRequested, Is.True);
                Assert.That(pending.IsFull, Is.True);
                Assert.That(pending.Causes, Is.EqualTo(SyncRunCause.RealtimeRemoteChange | SyncRunCause.LocalChange));
                Assert.That(pending.LocalChangedPaths, Is.EqualTo(local.LocalChangedPaths));
                Assert.That(pending.LocalDeletedPaths, Is.EqualTo(local.LocalDeletedPaths));
                Assert.That(pending.LocalRenames, Is.EqualTo(new[] { rename }));
            });
            Assert.That(queue.CompletePassOrTakeQueued(), Is.False);
        }

        [TestCase(SyncRunCause.RealtimeRemoteChange, false)]
        [TestCase(SyncRunCause.RealtimeRemoteChange, true)]
        [TestCase(SyncRunCause.Periodic, false)]
        [TestCase(SyncRunCause.Periodic, true)]
        public void QueueFullAndLocalChange_PreservesPendingPathsAndRenameOrder(
            SyncRunCause fullCause, bool fullFirst)
        {
            SyncPairRequestQueue queue = new(isBlocked: false);
            Assert.That(queue.TryStart(SyncRunRequest.Full), Is.True);
            LocalPathRename first = new("original.pdf", "rename.tmp");
            LocalPathRename second = new("rename.tmp", "final.pdf");
            SyncRunRequest full = SyncRunRequest.ForFull(fullCause);
            SyncRunRequest local = SyncRunRequest.ForLocalChangedPaths(
                [first.SourcePath, second.TargetPath, "deleted.txt"], ["deleted.txt"],
                localRenames: [first, second]);
            if (fullFirst)
            {
                queue.TryStart(full);
                queue.TryStart(local);
            }
            else
            {
                queue.TryStart(local);
                queue.TryStart(full);
            }

            Assert.That(queue.CompletePassOrTakeQueued(), Is.True);
            SyncRunRequest pending = queue.GetActiveRequest();
            Assert.Multiple(() =>
            {
                Assert.That(pending.IsFull, Is.True);
                Assert.That(pending.LocalChangedPaths, Is.EqualTo(local.LocalChangedPaths));
                Assert.That(pending.LocalDeletedPaths, Is.EqualTo(local.LocalDeletedPaths));
                Assert.That(pending.LocalRenames, Is.EqualTo(new[] { first, second }));
            });
        }

        [Test]
        public void QueuePathsBeyondLimit_KeepsRenameEvidenceForFullReconcile()
        {
            SyncPairRequestQueue queue = new(isBlocked: false);
            queue.TryStart(SyncRunRequest.Full);
            LocalPathRename rename = new("original.pdf", "final.pdf");
            string[] paths = Enumerable.Range(0, SyncRunRequest.MaximumQueuedScopedPaths + 1)
                .Select(static index => $"file-{index}.txt").ToArray();
            queue.TryStart(SyncRunRequest.ForLocalChangedPaths(paths, [], localRenames: [rename]));
            queue.TryStart(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.That(queue.CompletePassOrTakeQueued(), Is.True);
            SyncRunRequest pending = queue.GetActiveRequest();
            Assert.Multiple(() =>
            {
                Assert.That(pending.IsFull, Is.True);
                Assert.That(pending.LocalChangedPaths, Is.Empty);
                Assert.That(pending.Causes.HasFlag(SyncRunCause.LocalChangeOverflow), Is.True);
                Assert.That(pending.LocalRenames, Is.EqualTo(new[] { rename }));
            });
        }
    }
}
