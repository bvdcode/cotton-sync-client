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
        public void SyncNowAsync_SetsOfflineAndRethrowsWhenTransientNetworkFailurePersists()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("network down"),
                    new HttpRequestException("network down"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            HttpRequestException? exception = Assert.ThrowsAsync<HttpRequestException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
            });
        }

        [Test]
        public async Task SyncNowAsync_UsesCappedExponentialBackoffForNetworkTimeouts()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("request timed out", new TimeoutException()),
                    new HttpRequestException("request timed out", new TimeoutException()),
                    new HttpRequestException("request timed out", new TimeoutException()),
                ],
            };
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunnerRetryOptions retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 4,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, retryOptions, logger);

            await runner.SyncNowAsync();

            string[] retryMessages = logger.Entries
                .Where(entry => entry.Level == LogLevel.Warning)
                .Select(entry => entry.Message)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(4));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(retryMessages, Has.Length.EqualTo(3));
                Assert.That(retryMessages[0], Does.Contain("after 00:00:00.001"));
                Assert.That(retryMessages[1], Does.Contain("after 00:00:00.002"));
                Assert.That(retryMessages[2], Does.Contain("after 00:00:00.002"));
            });
        }

        [Test]
        public async Task PauseAsync_CancelsNetworkRetryBackoffPromptly()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("request timed out", new TimeoutException()),
                ],
            };
            SyncPairRunnerRetryOptions retryOptions = new SyncPairRunnerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(30),
                MaxDelay = TimeSpan.FromSeconds(30),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, retryOptions);

            Task activeSync = runner.SyncNowAsync();
            Assert.Multiple(() =>
            {
                Assert.That(activeSync.IsCompleted, Is.False);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
            });

            Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await runner.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await activeSync.WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            Assert.Multiple(() =>
            {
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Paused));
                Assert.That(runner.Status.LastError, Is.Null);
            });
        }

        [TestCase(System.Net.HttpStatusCode.InternalServerError)]
        [TestCase(System.Net.HttpStatusCode.BadGateway)]
        [TestCase(System.Net.HttpStatusCode.ServiceUnavailable)]
        [TestCase(System.Net.HttpStatusCode.GatewayTimeout)]
        public async Task SyncNowAsync_RetriesTransientApiFailureAndReturnsIdleOnRecovery(
            System.Net.HttpStatusCode statusCode)
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new CottonApiException(statusCode, "temporary server response", "request failed"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.LastError, Is.Null);
            });
        }

        [Test]
        public void SyncNowAsync_TreatsMissingChangeFeedRouteAsTemporaryOfflineState()
        {
            RemoteChangeFeedUnavailableException failure = new RemoteChangeFeedUnavailableException(
                new CottonApiException(
                    System.Net.HttpStatusCode.NotFound,
                    "404 page not found",
                    "Cotton API request GET /api/v1/sync/changes failed with status 404."));
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures = [failure, failure],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            RemoteChangeFeedUnavailableException? exception = Assert.ThrowsAsync<RemoteChangeFeedUnavailableException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud desktop change feed is temporarily unavailable. Cotton Sync will retry automatically."));
                Assert.That(runner.Status.CurrentOperation, Does.StartWith("Waiting for connection:"));
            });
        }

        [Test]
        public void SyncNowAsync_TreatsPersistentBadGatewayAsTemporaryOfflineState()
        {
            CottonApiException failure = new CottonApiException(
                System.Net.HttpStatusCode.BadGateway,
                "502 Bad Gateway",
                "Cotton API request failed with status 502.");
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures = [failure, failure],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            CottonApiException? exception = Assert.ThrowsAsync<CottonApiException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
                Assert.That(runner.Status.CurrentOperation, Does.StartWith("Waiting for connection:"));
            });
        }

        [Test]
        public void SyncNowAsync_TreatsGenericProxyNotFoundAsTemporaryOfflineState()
        {
            CottonApiException failure = new CottonApiException(
                System.Net.HttpStatusCode.NotFound,
                "404 page not found",
                "Cotton API request failed with status 404.");
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures = [failure, failure],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            Assert.ThrowsAsync<CottonApiException>(async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Offline));
                Assert.That(
                    runner.Status.LastError,
                    Is.EqualTo("Cotton Cloud is temporarily unavailable. Cotton Sync will retry automatically."));
            });
        }

        [Test]
        public void SyncNowAsync_KeepsStructuredNotFoundAsActionRequiredState()
        {
            CottonApiException failure = new CottonApiException(
                System.Net.HttpStatusCode.NotFound,
                "{\"error\":\"node not found\"}",
                "Cotton API request failed with status 404.");
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = failure,
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            Assert.ThrowsAsync<CottonApiException>(async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
            });
        }

        [Test]
        public async Task SyncNowAsync_ReturnsFromOfflineToIdleWhenNetworkRecovers()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("network down"),
                    new HttpRequestException("network down"),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions(maxAttempts: 2));

            Assert.ThrowsAsync<HttpRequestException>(
                async () => await runner.SyncNowAsync());
            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(3));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
                Assert.That(runner.Status.CurrentOperation, Is.Null);
                Assert.That(runner.Status.LastError, Is.Null);
                Assert.That(runner.Status.LastSuccessfulSyncAtUtc, Is.Not.Null);
            });
        }

        [Test]
        public void SyncNowAsync_FailureLogIncludesSyncPairId()
        {
            SyncPairSettings syncPair = CreatePair(isEnabled: true);
            RecordingLogger<SyncPairRunner> logger = new RecordingLogger<SyncPairRunner>();
            SyncPairRunner runner = CreateRunner(
                syncPair,
                new FakeSyncPairWork { Failure = new InvalidOperationException("sync failed") },
                logger: logger);

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runner.SyncNowAsync());

            Assert.That(
                logger.Entries.Select(entry => entry.Message),
                Has.Some.Contains(syncPair.Id.ToString()));
        }

    }
}
