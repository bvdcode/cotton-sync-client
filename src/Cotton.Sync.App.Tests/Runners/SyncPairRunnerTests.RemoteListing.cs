// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncPairRunnerTests
    {
        [Test]
        public async Task SyncNowAsync_RetriesRemoteListingChangesAndCompletesWithoutActionRequired()
        {
            FakeSyncPairWork work = new()
            {
                Failures =
                [
                    new RemoteDirectoryListingUnavailableException("Expected total 1688, received 1689."),
                    new RemoteDirectoryListingUnavailableException("Expected 234 entries, received 235."),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(3));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.LastError, Is.Null);
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Not.Null);
            });
        }

        [Test]
        public async Task SyncNowAsync_PersistentRemoteListingChangesWaitAndPreserveScopedWorkForPeriodicRecovery()
        {
            RemoteDirectoryListingUnavailableException failure = new("Expected 234 entries, received 235.");
            FakeSyncPairWork work = new() { Failures = [failure, failure] };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));
            LocalPathRename rename = new("original.txt", "renamed.txt");
            SyncRunRequest original = SyncRunRequest.ForLocalChangedPaths(
                ["renamed.txt", "deleted.txt"], ["deleted.txt"], localRenames: [rename]);

            Assert.ThrowsAsync<RemoteDirectoryListingUnavailableException>(() => runner.SyncNowAsync(original));

            Assert.Multiple(() =>
            {
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Waiting));
                Assert.That(runner.Status.LastError, Does.Contain("retry automatically"));
                Assert.That(runner.Status.CurrentOperation, Does.Not.Contain("Action required"));
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Null);
            });

            await runner.SyncNowAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic));

            Assert.Multiple(() =>
            {
                Assert.That(work.Requests[2], Is.SameAs(original));
                Assert.That(work.Requests[2].LocalRenames, Is.EqualTo(new[] { rename }));
                Assert.That(work.Requests[2].LocalDeletedPaths, Is.EqualTo(new[] { "deleted.txt" }));
                Assert.That(work.Requests[3].Causes, Is.EqualTo(SyncRunCause.Periodic));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.LastError, Is.Null);
            });
        }
    }
}
