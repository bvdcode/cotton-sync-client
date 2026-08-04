// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.Tests.Runners
{
    public class SyncRunRequestTests
    {
        [Test]
        public void ForLocalChangedPaths_RejectsEmptyScope()
        {
            Assert.Throws<ArgumentException>(() => SyncRunRequest.ForLocalChangedPaths(Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => SyncRunRequest.ForLocalChangedPaths(["", " "]));
        }

        [Test]
        public void Merge_PreservesAllCausesAndRequiredFullScope()
        {
            SyncRunRequest scoped = SyncRunRequest.ForLocalChangedPaths(
                ["Docs/report.txt", "Docs/deleted.txt"],
                ["Docs/deleted.txt"],
                SyncRunCause.LocalChange);
            SyncRunRequest full = SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange);

            SyncRunRequest merged = scoped.Merge(full);

            Assert.Multiple(() =>
            {
                Assert.That(merged.IsFull, Is.True);
                Assert.That(merged.LocalChangedPaths, Is.EqualTo(new[] { "Docs/deleted.txt", "Docs/report.txt" }));
                Assert.That(merged.LocalDeletedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
                Assert.That(
                    merged.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.RealtimeRemoteChange));
            });
        }

        [Test]
        public void ForLocalChangedPaths_IncludesDeletedPathsInScope()
        {
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(
                ["Docs/report.txt"],
                ["Docs/deleted.txt"]);

            Assert.Multiple(() =>
            {
                Assert.That(request.IsFull, Is.False);
                Assert.That(request.LocalChangedPaths, Is.EqualTo(new[] { "Docs/deleted.txt", "Docs/report.txt" }));
                Assert.That(request.LocalDeletedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
            });
        }

        [Test]
        public void ForFull_RejectsMissingCause()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SyncRunRequest.ForFull(SyncRunCause.None));
        }

        [Test]
        public void ForFull_RejectsNonPositiveRemoteDeleteApproval()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SyncRunRequest.ForFull(SyncRunCause.Manual, 0));
        }

        [Test]
        public void Merge_PreservesOnlyMatchingRemoteDeleteApproval()
        {
            SyncRunRequest approved = SyncRunRequest.ForFull(SyncRunCause.Manual, 101);

            SyncRunRequest matching = approved.Merge(SyncRunRequest.ForFull(SyncRunCause.Manual, 101));
            SyncRunRequest changed = approved.Merge(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(matching.ApprovedRemoteDeleteCount, Is.EqualTo(101));
                Assert.That(changed.ApprovedRemoteDeleteCount, Is.Null);
            });
        }
    }
}
