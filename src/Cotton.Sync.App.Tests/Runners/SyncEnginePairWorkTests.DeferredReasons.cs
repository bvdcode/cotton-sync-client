// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncEnginePairWorkTests
    {
        [TestCase("The file changed during scanning.")]
        [TestCase("The cloud file provider is not running.")]
        [TestCase("The process cannot access the file because it is being used by another process.")]
        public void RunOnceAsync_ReportsActualDeferredReasonWithoutAssumingOpenApplications(string reason)
        {
            SyncRunResult result = new();
            result.RecordDeferredLocalPath("Documents/report.txt");
            result.Activities.Add(new SyncActivity
            {
                Kind = SyncActivityKind.Skipped,
                RelativePath = "Documents/report.txt",
                Details = reason,
            });
            FakeSyncEngine engine = new() { ResultToReturn = result };
            SyncEnginePairWork work = new(engine, delayAsync: (_, _) => Task.CompletedTask);

            SyncActionRequiredException? exception = Assert.ThrowsAsync<SyncActionRequiredException>(
                () => work.RunOnceAsync(CreateSyncPair(Guid.NewGuid())));
            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain(reason));
                Assert.That(exception.Message, Does.Contain("Documents/report.txt"));
                Assert.That(exception.Message, Does.Not.Contain("Close applications"));
                Assert.That(engine.RunOnceCallCount, Is.EqualTo(4));
            });
        }
    }
}
