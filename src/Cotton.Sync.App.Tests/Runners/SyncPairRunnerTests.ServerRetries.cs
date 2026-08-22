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
        public void SyncNowAsync_SetsErrorAndRethrowsOnFailure()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = new InvalidOperationException("sync failed"),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo("sync failed"));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsRemoteQuotaAsActionRequiredMessage()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = new CottonApiException(
                    (System.Net.HttpStatusCode)507,
                    null,
                    "Cotton API request failed with status 507."),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            CottonApiException? exception = Assert.ThrowsAsync<CottonApiException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Remote storage quota exceeded. Free space in Cotton Cloud or choose a smaller sync folder.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsLocalPermissionDeniedAsActionRequiredMessage()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = new UnauthorizedAccessException("Access to the path was denied."),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            UnauthorizedAccessException? exception = Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Permission denied while accessing local sync files. Check folder permissions and retry.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsLocalDiskFullAsActionRequiredMessage()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = new TestIOException(unchecked((int)0x80070070)),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            IOException? exception = Assert.ThrowsAsync<TestIOException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Local disk is full. Free space on this computer and retry sync.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public void SyncNowAsync_ReportsPreflightLocalDiskFullAsActionRequiredMessage()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = new LocalInsufficientDiskSpaceException("Videos/big.bin", requiredBytes: 200, availableBytes: 100),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work);

            LocalInsufficientDiskSpaceException? exception = Assert.ThrowsAsync<LocalInsufficientDiskSpaceException>(
                async () => await runner.SyncNowAsync());

            const string expected = "Local disk is full. Free space on this computer and retry sync.";
            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [TestCase(System.Net.HttpStatusCode.InternalServerError)]
        [TestCase(System.Net.HttpStatusCode.ServiceUnavailable)]
        [TestCase(System.Net.HttpStatusCode.Locked)]
        public async Task SyncNowAsync_RetriesTransientServerFailureAndReturnsIdleOnRecovery(System.Net.HttpStatusCode statusCode)
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("server unavailable", null, statusCode),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [Test]
        public async Task SyncNowAsync_RetriesRateLimitAndReturnsIdleOnRecovery()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

        [TestCase(
            System.Net.HttpStatusCode.Unauthorized,
            "Session expired. Sign in again to continue syncing.",
            TestName = "SyncNowAsync_ReportsExpiredSessionAsActionRequiredMessage")]
        [TestCase(
            System.Net.HttpStatusCode.Forbidden,
            "Cotton Cloud denied access to this sync folder. Check account permissions and sign in again if needed.",
            TestName = "SyncNowAsync_ReportsForbiddenServerResponseAsActionRequiredMessage")]
        [TestCase(
            System.Net.HttpStatusCode.Conflict,
            "Cotton Cloud reported a conflict while syncing. Review conflicts and retry.",
            TestName = "SyncNowAsync_ReportsServerConflictAsActionRequiredMessage")]
        public void SyncNowAsync_ReportsNonRetriableServerResponseAsActionRequiredMessage(
            System.Net.HttpStatusCode statusCode,
            string expected)
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failure = new CottonApiException(
                    statusCode,
                    "{\"success\":false,\"message\":\"server rejected sync\"}",
                    "Cotton API request failed with status " + (int)statusCode + "."),
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            CottonApiException? exception = Assert.ThrowsAsync<CottonApiException>(
                async () => await runner.SyncNowAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(work.RunCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Error));
                Assert.That(runner.Status.LastError, Is.EqualTo(expected));
                Assert.That(runner.Status.CurrentOperation, Is.EqualTo("Action required: " + expected));
            });
        }

        [Test]
        public async Task SyncNowAsync_RetriesHttpTimeoutAndReturnsIdleOnRecovery()
        {
            FakeSyncPairWork work = new FakeSyncPairWork
            {
                Failures =
                [
                    new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."),
                ],
            };
            SyncPairRunner runner = CreateRunner(CreatePair(isEnabled: true), work, NoDelayRetryOptions());

            await runner.SyncNowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(work.RunCount, Is.EqualTo(2));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Idle));
            });
        }

    }
}
