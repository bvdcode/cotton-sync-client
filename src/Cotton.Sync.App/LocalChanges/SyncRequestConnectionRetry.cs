// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Supervision;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.LocalChanges
{
    internal class SyncRequestConnectionRetry
    {
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly ILogger _logger;
        private readonly TimeSpan _retryInterval;
        private readonly ISyncSupervisor _supervisor;

        public SyncRequestConnectionRetry(
            ISyncSupervisor supervisor,
            TimeSpan retryInterval,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            ILogger logger)
        {
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            _retryInterval = retryInterval;
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RequestAsync(
            Guid syncPairId,
            SyncRunRequest request,
            string operation,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                try
                {
                    await _supervisor.SyncNowAsync(syncPairId, request, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (SyncFailureClassifier.IsTransientConnectionFailure(exception))
                {
                    _logger.LogWarning(
                        exception,
                        "{Operation} for {SyncPairId} could not reach Cotton Cloud; retrying after {RetryInterval}.",
                        operation,
                        syncPairId,
                        _retryInterval);
                    await _delayAsync(_retryInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
