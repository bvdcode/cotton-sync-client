// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncPairRunnerTests
    {
        [Test]
        public async Task SyncNowAsync_CoalescesOverlappingRequestsIntoOneQueuedRun()
        {
            BlockingSyncPairWork work = new BlockingSyncPairWork();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task first = runner.SyncNowAsync();
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            Task second = runner.SyncNowAsync();
            Task third = runner.SyncNowAsync();

            await Task.WhenAll(second, third);
            work.ReleaseCurrentRun();
            await work.WaitForRunCountAsync(2, TimeSpan.FromSeconds(2));
            work.ReleaseCurrentRun();
            await first;

            Assert.That(work.RunCount, Is.EqualTo(2));
        }

        [Test]
        public async Task SyncNowAsync_CoalescesQueuedRealtimeAndPeriodicFullRequests()
        {
            BlockingSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            Task activeSync = runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Manual));
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.RealtimeRemoteChange));
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            work.ReleaseCurrentRun();
            await work.WaitForRunCountAsync(2, TimeSpan.FromSeconds(2));
            work.ReleaseCurrentRun();
            await activeSync;

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[1].IsFull, Is.True);
                Assert.That(
                    work.Requests[1].Causes,
                    Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.RealtimeRemoteChange));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_CoalescesRepeatedScopedEditsWhileAnotherUploadIsActive()
        {
            BlockingSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            SyncRunRequest blockerRequest = SyncRunRequest.ForLocalChangedPaths(["active-upload-blocker.bin"]);
            SyncRunRequest pinnedEditRequest = SyncRunRequest.ForLocalChangedPaths(["pinned-edit.bin"]);

            Task activeSync = runner.SyncNowAsync(blockerRequest);
            await work.WaitForRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(pinnedEditRequest);
            await runner.SyncNowAsync(pinnedEditRequest);
            await runner.SyncNowAsync(pinnedEditRequest);

            work.ReleaseCurrentRun();
            await work.WaitForRunCountAsync(2, TimeSpan.FromSeconds(2));
            work.ReleaseCurrentRun();
            await activeSync;

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "active-upload-blocker.bin" }));
                Assert.That(work.Requests[1].LocalChangedPaths, Is.EqualTo(new[] { "pinned-edit.bin" }));
                Assert.That(work.Requests[1].IsFull, Is.False);
                Assert.That(work.Requests[1].Causes, Is.EqualTo(SyncRunCause.LocalChange));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_ScopedRequestSupersedesBackgroundFullPass()
        {
            PreemptibleSyncPairWork work = new();
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);
            SyncRunRequest backgroundRequest = SyncRunRequest.ForFull(SyncRunCause.Periodic);
            SyncRunRequest scopedRequest = SyncRunRequest.ForLocalChangedPaths(["Pictures/album"]);

            Task backgroundSync = runner.SyncNowAsync(backgroundRequest);
            await work.WaitForFirstRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(scopedRequest);
            await backgroundSync.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(2));
                Assert.That(work.Requests[0], Is.SameAs(backgroundRequest));
                Assert.That(work.Requests[1], Is.SameAs(scopedRequest));
                Assert.That(work.FirstRunCancellationObserved, Is.True);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_PreservesActionRequiredStateUntilManualFullRetrySucceeds()
        {
            FakeSyncPairWork work = new()
            {
                Failures = [new SyncActionRequiredException(
                    "Local delete blocked by mass-delete guard. 1040 pending deletes exceed limit 100.")],
            };
            SyncPairRunner runner = CreateRunner(
                CreatePair(isEnabled: true),
                work,
                NoDelayRetryOptions(maxAttempts: 1));
            SyncRunRequest backgroundRequest = SyncRunRequest.ForFull(SyncRunCause.Periodic);
            SyncRunRequest scopedRequest = SyncRunRequest.ForLocalChangedPaths(["Pictures/album"]);

            Assert.ThrowsAsync<SyncActionRequiredException>(
                async () => await runner.SyncNowAsync(backgroundRequest));
            await runner.SyncNowAsync(scopedRequest);

            SyncPairStatus statusAfterScopedSync = runner.Status;
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));
            SyncPairStatus statusAfterPeriodicSync = runner.Status;
            await runner.PauseAsync();
            await runner.ResumeAsync();
            SyncPairStatus statusAfterResume = runner.Status;
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Manual));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(4));
                Assert.That(work.Requests[0], Is.SameAs(backgroundRequest));
                Assert.That(work.Requests[1], Is.SameAs(scopedRequest));
                Assert.That(work.Requests[2].Causes, Is.EqualTo(SyncRunCause.Periodic));
                Assert.That(work.Requests[3].Causes, Is.EqualTo(SyncRunCause.Manual));
                Assert.That(statusAfterScopedSync.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(statusAfterScopedSync.LastError, Does.StartWith("Local delete blocked"));
                Assert.That(statusAfterPeriodicSync.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(statusAfterPeriodicSync.LastError, Is.EqualTo(statusAfterScopedSync.LastError));
                Assert.That(statusAfterResume.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(statusAfterResume.LastError, Is.EqualTo(statusAfterScopedSync.LastError));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.LastError, Is.Null);
            });
        }

        [Test]
        public async Task SyncNowAsync_RemoteDeleteApprovalReplaysExactActionRequiredScope()
        {
            RemoteDeletePlanApproval approval = new RemoteDeletePlanApproval(102, new string('a', 64));
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures = [new SyncActionRequiredException(
                    "Remote delete blocked by mass-delete guard. 102 pending deletes exceed limit 100.")],
            };
            SyncPairRunner runner = CreateRunner(
                CreatePair(isEnabled: true),
                work,
                NoDelayRetryOptions(maxAttempts: 1));
            SyncRunRequest blockedRequest = SyncRunRequest.ForLocalChangedPaths(
                ["MassDelete", "MassDelete/file.txt"],
                ["MassDelete", "MassDelete/file.txt"]);

            Assert.ThrowsAsync<SyncActionRequiredException>(
                async () => await runner.SyncNowAsync(blockedRequest));
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));
            SyncPairStatus statusAfterBackgroundSuccess = runner.Status;
            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Manual, approval));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(3));
                Assert.That(statusAfterBackgroundSuccess.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(work.Requests[2].IsFull, Is.False);
                Assert.That(work.Requests[2].LocalChangedPaths, Is.EqualTo(blockedRequest.LocalChangedPaths));
                Assert.That(work.Requests[2].LocalDeletedPaths, Is.EqualTo(blockedRequest.LocalDeletedPaths));
                Assert.That(work.Requests[2].Causes, Is.EqualTo(blockedRequest.Causes));
                Assert.That(work.Requests[2].ApprovedRemoteDeletePlan, Is.EqualTo(approval));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_MergesQueuedScopedRequestsIntoLaterFullCheck()
        {
            BlockingFirstFailureSyncPairWork work = new BlockingFirstFailureSyncPairWork();
            SyncPairRunner runner = CreateRunner(
                CreatePair(isEnabled: true),
                work,
                NoDelayRetryOptions(maxAttempts: 1));
            SyncRunRequest firstRequest = SyncRunRequest.ForLocalChangedPaths(["Docs/first.txt"]);
            SyncRunRequest secondRequest = SyncRunRequest.ForLocalChangedPaths(["Docs/second.txt"]);

            Task first = runner.SyncNowAsync(firstRequest);
            await work.WaitForFirstRunAsync(TimeSpan.FromSeconds(2));
            await runner.SyncNowAsync(secondRequest);
            work.ReleaseFirstRun();
            Assert.ThrowsAsync<HttpRequestException>(async () => await first);

            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests, Has.Count.EqualTo(3));
                Assert.That(work.Requests[0].LocalChangedPaths, Is.EqualTo(new[] { "Docs/first.txt" }));
                Assert.That(work.Requests[1].LocalChangedPaths, Is.EqualTo(new[] { "Docs/first.txt" }));
                Assert.That(work.Requests[2].IsFull, Is.True);
                Assert.That(work.Requests[2].Causes, Is.EqualTo(SyncRunCause.Periodic | SyncRunCause.LocalChange));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }
    }
}
