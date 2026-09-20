// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public class LocalChangeRequestFactoryRenameTests
    {
        [TestCase(SyncPairMode.FullMirror, false)]
        [TestCase(SyncPairMode.WindowsVirtualFiles, false)]
        [TestCase(SyncPairMode.FullMirror, true)]
        [TestCase(SyncPairMode.WindowsVirtualFiles, true)]
        public void Create_PreservesRenameChainThroughIgnoredTemporaryName(SyncPairMode mode, bool fullReconcile)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rename-request"));
            Guid pairId = Guid.NewGuid();
            using CancellationTokenSource cancellation = new();
            PendingLocalSyncRequest pending = new(cancellation, DateTimeOffset.UtcNow);
            LocalChangeRequestFactory.Record(root, mode, pending, new LocalSyncRootChange(pairId,
                Path.Combine(root, "rename.tmp"), LocalSyncRootChangeKind.Renamed, Path.Combine(root, "original.pdf")));
            LocalChangeRequestFactory.Record(root, mode, pending, new LocalSyncRootChange(pairId,
                Path.Combine(root, "final.pdf"), LocalSyncRootChangeKind.Renamed, Path.Combine(root, "rename.tmp")));
            LocalChangeRequestFactory.Record(root, mode, pending, new LocalSyncRootChange(pairId,
                Path.Combine(root, "final.pdf"), LocalSyncRootChangeKind.Changed));
            if (fullReconcile)
            {
                LocalChangeRequestFactory.Record(root, mode, pending,
                    new LocalSyncRootChange(pairId, root, LocalSyncRootChangeKind.Error));
            }

            SyncRunRequest request = LocalChangeRequestFactory.Create(root, mode, pending)!;

            Assert.Multiple(() =>
            {
                Assert.That(request.IsFull, Is.EqualTo(fullReconcile));
                Assert.That(request.LocalRenames.Select(rename => (rename.SourcePath, rename.TargetPath)),
                    Is.EqualTo(new[] { ("original.pdf", "rename.tmp"), ("rename.tmp", "final.pdf") }));
                Assert.That(request.LocalChangedPaths, Does.Not.Contain("rename.tmp"));
                Assert.That(request.LocalDeletedPaths, Is.Empty);
            });
        }
    }
}
