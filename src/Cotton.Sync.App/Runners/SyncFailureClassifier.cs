// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sdk;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Classifies failures that can resolve when the server or connection recovers.
    /// </summary>
    public static class SyncFailureClassifier
    {
        /// <summary>
        /// Returns whether a failure should remain in automatic connection recovery.
        /// </summary>
        public static bool IsTransientConnectionFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return exception switch
            {
                AggregateException aggregateException when aggregateException.InnerExceptions.Count > 0
                    => aggregateException.InnerExceptions.All(IsTransientConnectionFailure),
                RemoteChangeFeedUnavailableException => true,
                CottonApiException apiException => IsTransientApiFailure(apiException),
                HttpRequestException requestException => IsTransientStatusCode(requestException.StatusCode),
                TimeoutException => true,
                TaskCanceledException => true,
                _ => false,
            };
        }

        private static bool IsTransientApiFailure(CottonApiException exception)
        {
            return IsTransientStatusCode(exception.StatusCode)
                || (exception.StatusCode == HttpStatusCode.NotFound
                    && string.Equals(
                        exception.ResponseBody?.Trim(),
                        "404 page not found",
                        StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTransientStatusCode(HttpStatusCode? statusCode)
        {
            return statusCode is null
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.Locked
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }
    }
}
