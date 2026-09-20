// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public async Task AvailabilityRouting_PreservesObservedRenames(bool fullReconcile, bool includePaths)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = new(
                inner, new FakeSyncStateStore(), new FakeCloudFilesAdapter(),
                readDiskState: static _ => null);
            LocalPathRename rename = new("original.pdf", "final.pdf");
            SyncRunRequest request = SyncRunRequest.ForFull(SyncRunCause.Manual, localRenames: [rename]);
            if (includePaths)
            {
                SyncRunRequest local = SyncRunRequest.ForLocalChangedPaths(
                    [".", rename.SourcePath, rename.TargetPath], [], localRenames: [rename]);
                request = fullReconcile ? request.Merge(local) : local;
            }

            await work.RunOnceAsync(pair, request);

            Assert.That(inner.Requests, Has.Count.EqualTo(1));
            Assert.That(inner.Requests[0].LocalRenames, Is.EqualTo(new[] { rename }));
        }
    }
}
