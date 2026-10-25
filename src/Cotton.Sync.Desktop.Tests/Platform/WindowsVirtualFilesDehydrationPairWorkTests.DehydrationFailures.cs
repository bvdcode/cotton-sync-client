// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RunOnceAsync_DehydrationLimitationPreservesStateAndContinuesOtherFilesAndSync(bool full)
        {
            const string failedPath = "Lessons/01.mp4";
            const string nextPath = "Lessons/02.mp4";
            const string changedPath = "Projects/new.txt";
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore store = new();
            SyncStateEntry failed = CreatePlaceholderState(pair, failedPath);
            failed.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            failed.LocalContentHash = failed.RemoteContentHash;
            failed.LocalSizeBytes = 12;
            DateTime writeTime = new(2026, 6, 16, 10, 5, 0, DateTimeKind.Utc);
            failed.LocalLastWriteUtc = writeTime;
            store.UpsertEntry(failed);
            store.UpsertEntry(CreatePlaceholderState(pair, nextPath));
            FakeCloudFilesAdapter cloudFiles = new()
            {
                Dehydrating = path =>
                {
                    if (path == failedPath)
                    {
                        throw new WindowsCloudFilesNativeException("CfDehydratePlaceholder", unchecked((int)0x80070299));
                    }
                },
            };
            WindowsCloudFilesDiagnostics diagnostics = new();
            RecordingRunProgressPublisher progress = new();
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles, diagnostics: diagnostics,
                readDiskState: _ => CreateUnpinnedHydratedDiskState(), runProgressPublisher: progress);
            SyncRunRequest request = SyncRunRequest.ForLocalChangedPaths([failedPath, nextPath, changedPath]);
            if (full)
            {
                request = request.Merge(SyncRunRequest.ForFull(SyncRunCause.LocalChange));
            }

            SyncActionRequiredException? exception = Assert.ThrowsAsync<SyncActionRequiredException>(
                () => work.RunOnceAsync(pair, request));

            SyncStateEntry unchanged = store.GetRequired(pair.Id, failedPath);
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot()
                .Single(item => item.Status == "deferred");
            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.StartWith("Windows could not free up space"));
                Assert.That(exception.Message, Does.Contain(failedPath));
                Assert.That(exception.Message, Does.Not.Contain(nextPath));
                Assert.That(cloudFiles.DehydratedPaths, Is.EqualTo(new[] { nextPath }));
                Assert.That(unchanged.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(unchanged.LocalContentHash, Is.EqualTo("remote-hash"));
                Assert.That(unchanged.LocalSizeBytes, Is.EqualTo(12));
                Assert.That(unchanged.LocalLastWriteUtc, Is.EqualTo(writeTime));
                Assert.That(unchanged.PlaceholderIdentity, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(inner.Requests, Has.Count.EqualTo(1));
                Assert.That(inner.Requests[0].IsFull, Is.EqualTo(full));
                Assert.That(inner.Requests[0].LocalChangedPaths, Does.Contain(changedPath).And.Contain(failedPath));
                Assert.That(inner.Requests[0].LocalChangedPaths, Does.Not.Contain(nextPath));
                Assert.That(diagnostic.RelativePath, Is.EqualTo(failedPath));
                Assert.That(diagnostic.HResult, Is.EqualTo(unchecked((int)0x80070299)));
                Assert.That(progress.Progress.Last().FilesCompleted, Is.EqualTo(1));
                Assert.That(progress.Progress.Last().FilesTotal, Is.EqualTo(2));
                Assert.That(progress.Progress.Last().IsCompleted, Is.True);
            });
        }

        [TestCase("CfDehydratePlaceholder", unchecked((int)0x80070005))]
        [TestCase("CfSetInSyncState", unchecked((int)0x80070299))]
        public void RunOnceAsync_UnrelatedNativeFailureIsNotReclassified(string operation, int hresult)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore store = new();
            store.UpsertEntry(CreatePlaceholderState(pair, "Lessons/file.mp4"));
            WindowsCloudFilesNativeException failure = new(operation, hresult);
            FakeCloudFilesAdapter cloudFiles = new() { Dehydrating = _ => throw failure };
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles, readDiskState: _ => CreateUnpinnedHydratedDiskState());

            WindowsCloudFilesNativeException? thrown = Assert.ThrowsAsync<WindowsCloudFilesNativeException>(
                () => work.RunOnceAsync(pair, SyncRunRequest.ForLocalChangedPaths(["Lessons/file.mp4"])));

            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(inner.Requests, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RunOnceAsync_MultipleDehydrationFailuresDoNotHidePrimarySyncFailure(bool syncFails)
        {
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore store = new();
            store.UpsertEntry(CreatePlaceholderState(pair, "Lessons/01.mp4"));
            store.UpsertEntry(CreatePlaceholderState(pair, "Lessons/02.mp4"));
            FakeCloudFilesAdapter cloudFiles = new()
            {
                Dehydrating = _ => throw new WindowsCloudFilesNativeException(
                    "CfDehydratePlaceholder", unchecked((int)0x80070299)),
            };
            SyncActionRequiredException primaryFailure = new("Remote deletion requires review.");
            RecordingSyncPairWork inner = new() { Failure = syncFails ? primaryFailure : null };
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles, diagnostics: diagnostics,
                readDiskState: _ => CreateUnpinnedHydratedDiskState());

            SyncActionRequiredException? failure = Assert.ThrowsAsync<SyncActionRequiredException>(() =>
                work.RunOnceAsync(pair, SyncRunRequest.ForLocalChangedPaths(["Lessons/01.mp4", "Lessons/02.mp4"])));

            Assert.That(inner.Requests, Has.Count.EqualTo(1));
            Assert.That(diagnostics.Snapshot().Count(item => item.Status == "deferred"), Is.EqualTo(2));
            if (syncFails)
            {
                Assert.That(failure, Is.SameAs(primaryFailure));
            }
            else
            {
                Assert.That(failure!.Message, Does.Contain("Lessons/01.mp4").And.Contain("Lessons/02.mp4"));
            }
        }

        [Test]
        public void RunOnceAsync_CancellationDuringDehydrationDoesNotContinueOrChangeState()
        {
            using CancellationTokenSource cancellation = new();
            SyncPairSettings pair = CreateVirtualFilesPair();
            FakeSyncStateStore store = new();
            SyncStateEntry state = CreatePlaceholderState(pair, "Lessons/file.mp4");
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            store.UpsertEntry(state);
            FakeCloudFilesAdapter cloudFiles = new()
            {
                Dehydrating = _ =>
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                },
            };
            RecordingSyncPairWork inner = new();
            WindowsVirtualFilesDehydrationPairWork work = CreateWork(
                inner, store, cloudFiles, readDiskState: _ => CreateUnpinnedHydratedDiskState());

            Assert.ThrowsAsync<OperationCanceledException>(() => work.RunOnceAsync(
                pair, SyncRunRequest.ForLocalChangedPaths(["Lessons/file.mp4", "Projects/new.txt"]), cancellation.Token));

            Assert.That(inner.Requests, Is.Empty);
            Assert.That(state.PlaceholderHydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
        }
    }
}
