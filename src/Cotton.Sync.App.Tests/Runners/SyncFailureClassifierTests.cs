// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Runners;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Tests.Runners
{
    public class SyncFailureClassifierTests
    {
        [Test]
        public void IsTransientSyncFailure_RetriesRemoteListingWithoutReportingConnectionLoss()
        {
            RemoteDirectoryListingUnavailableException exception = new("Remote listing changed.");

            Assert.Multiple(() =>
            {
                Assert.That(SyncFailureClassifier.IsTransientSyncFailure(exception), Is.True);
                Assert.That(SyncFailureClassifier.IsTransientRemoteListingFailure(exception), Is.True);
                Assert.That(SyncFailureClassifier.IsTransientConnectionFailure(exception), Is.False);
                Assert.That(SyncFailureClassifier.IsTransientSyncFailure(new IOException("Disk failure.")), Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void IsTransientSyncFailure_RetriesAggregateOnlyWhenEveryFailureCanRecover(bool includeLocalFailure)
        {
            AggregateException exception = new(
                new RemoteDirectoryListingUnavailableException("Remote listing changed."),
                includeLocalFailure ? new IOException("Disk failure.") : new TimeoutException());

            Assert.Multiple(() =>
            {
                Assert.That(SyncFailureClassifier.IsTransientSyncFailure(exception), Is.EqualTo(!includeLocalFailure));
                Assert.That(SyncFailureClassifier.IsTransientRemoteListingFailure(exception), Is.EqualTo(!includeLocalFailure));
            });
        }

        [TestCase(HttpRequestError.ResponseEnded, true)]
        [TestCase(HttpRequestError.InvalidResponse, false)]
        [TestCase(HttpRequestError.Unknown, false)]
        public void IsTransientConnectionFailure_ClassifiesHttpBodyReadError(
            HttpRequestError requestError,
            bool expected)
        {
            HttpIOException exception = new(requestError, "The response ended prematurely.");

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.EqualTo(expected));
        }

        [Test]
        public void IsTransientConnectionFailure_DoesNotRetryLocalIoFailure()
        {
            IOException exception = new("The local file could not be written.");

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.False);
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void IsTransientConnectionFailure_DoesNotRetryDeniedApiRequest(HttpStatusCode statusCode)
        {
            CottonApiException exception = new(statusCode, null, "Access denied.");

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.False);
        }

        [Test]
        public void IsTransientConnectionFailure_DoesNotRetryOperationCancellation()
        {
            OperationCanceledException exception = new();

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.False);
        }

        [Test]
        public void IsTransientConnectionFailure_KeepsHttpClientTimeoutRetryable()
        {
            TaskCanceledException exception = new("The request exceeded HttpClient.Timeout.");

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.True);
        }

        [Test]
        public void IsTransientConnectionFailure_RetriesAggregateOfTruncatedResponses()
        {
            AggregateException exception = new(
                new HttpIOException(HttpRequestError.ResponseEnded),
                new HttpIOException(HttpRequestError.ResponseEnded));

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.True);
        }

        [Test]
        public void IsTransientConnectionFailure_DoesNotRetryMixedNetworkAndLocalFailures()
        {
            AggregateException exception = new(
                new HttpIOException(HttpRequestError.ResponseEnded),
                new IOException("The local file could not be written."));

            bool transient = SyncFailureClassifier.IsTransientConnectionFailure(exception);

            Assert.That(transient, Is.False);
        }
    }
}
