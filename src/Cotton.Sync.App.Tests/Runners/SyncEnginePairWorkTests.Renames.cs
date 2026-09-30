// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncEnginePairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RunOnceAsync_PropagatesObservedRenamesToCore(bool fullReconcile)
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            LocalPathRename rename = new("original.pdf", "final.pdf");
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(
                [rename.SourcePath, rename.TargetPath], [], localRenames: [rename]);
            if (fullReconcile)
            {
                request = request.Merge(SyncRunRequest.Full);
            }

            await work.RunOnceAsync(CreateSyncPair(Guid.NewGuid()), request);

            Assert.That(engine.LastOptions!.Scope.LocalRenames, Is.EqualTo(new[] { rename }));
        }

        [Test]
        public async Task FullRunWithoutPublishers_PreservesObservedRenames()
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            LocalPathRename rename = new("original.pdf", "final.pdf");

            await work.RunOnceAsync(CreateSyncPair(Guid.NewGuid()),
                SyncRunRequest.ForFull(SyncRunCause.Manual, localRenames: [rename]));

            Assert.That(engine.LastOptions?.Scope.LocalRenames, Is.EqualTo(new[] { rename }));
        }

        [TestCase(SyncRunCause.Manual)]
        [TestCase(SyncRunCause.Periodic)]
        public async Task FullRun_PreservesObservedChangesAndDeletes(SyncRunCause cause)
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(
                ["Docs/report.txt"], ["Docs/deleted.txt"]).Merge(SyncRunRequest.ForFull(cause));

            await work.RunOnceAsync(CreateSyncPair(Guid.NewGuid()), request);

            Assert.Multiple(() =>
            {
                Assert.That(engine.LastOptions, Is.Not.Null);
                Assert.That(engine.LastOptions!.Scope.IsFull, Is.True);
                Assert.That(engine.LastOptions.Scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/deleted.txt", "Docs/report.txt" }));
                Assert.That(engine.LastOptions.Scope.LocalDeletedPaths, Is.EqualTo(new[] { "Docs/deleted.txt" }));
            });
        }

        [Test]
        public async Task FullRun_WithRootMetadataEventPreservesConcreteChanges()
        {
            FakeSyncEngine engine = new();
            SyncEnginePairWork work = new(engine);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths(
                [".", "Docs/report.txt"]).Merge(SyncRunRequest.Full);

            await work.RunOnceAsync(CreateSyncPair(Guid.NewGuid()), request);

            Assert.That(engine.LastOptions!.Scope.LocalChangedPaths, Is.EqualTo(new[] { "Docs/report.txt" }));
        }
    }
}
