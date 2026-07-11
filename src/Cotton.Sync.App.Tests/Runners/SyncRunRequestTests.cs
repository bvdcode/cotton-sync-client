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
                ["Docs/report.txt"],
                SyncRunCause.LocalChange);
            SyncRunRequest full = SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange);

            SyncRunRequest merged = scoped.Merge(full);

            Assert.Multiple(() =>
            {
                Assert.That(merged.IsFull, Is.True);
                Assert.That(merged.LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
                Assert.That(
                    merged.Causes,
                    Is.EqualTo(SyncRunCause.LocalChange | SyncRunCause.RealtimeRemoteChange));
            });
        }

        [Test]
        public void ForFull_RejectsMissingCause()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SyncRunRequest.ForFull(SyncRunCause.None));
        }
    }
}
