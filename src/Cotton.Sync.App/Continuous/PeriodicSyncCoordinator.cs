// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.Runners;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.App.Continuous
{
    /// <summary>
    /// Requests periodic remote change-feed checks as a safety fallback.
    /// </summary>
    public class PeriodicSyncCoordinator : IPeriodicSyncCoordinator
    {
        /// <summary>
        /// Default periodic change-feed check interval.
        /// </summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Default retry interval while Cotton Cloud is temporarily unavailable.
        /// </summary>
        public static readonly TimeSpan DefaultConnectionRetryInterval = TimeSpan.FromSeconds(15);

        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private readonly TimeSpan _connectionRetryInterval;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly TimeSpan _interval;
        private readonly ILogger<PeriodicSyncCoordinator> _logger;
        private readonly bool _runImmediately;
        private readonly ISyncSupervisor _supervisor;
        private CancellationTokenSource? _lifetime;
        private Task? _runner;

        /// <summary>
        /// Initializes a new instance of the <see cref="PeriodicSyncCoordinator" /> class.
        /// </summary>
        public PeriodicSyncCoordinator(
            ISyncSupervisor supervisor,
            TimeSpan? interval = null,
            bool runImmediately = true,
            ILogger<PeriodicSyncCoordinator>? logger = null,
            TimeSpan? connectionRetryInterval = null,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        {
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            _interval = interval ?? DefaultInterval;
            if (_interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "Periodic sync interval must be positive.");
            }

            _connectionRetryInterval = connectionRetryInterval ?? DefaultConnectionRetryInterval;
            if (_connectionRetryInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(connectionRetryInterval),
                    "Connection retry interval must be positive.");
            }

            _runImmediately = runImmediately;
            _logger = logger ?? NullLogger<PeriodicSyncCoordinator>.Instance;
            _delayAsync = delayAsync ?? Task.Delay;
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                _lifetime = new CancellationTokenSource();
                _runner = RunLoopAsync(_lifetime.Token);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task StopCoreAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource? lifetime = _lifetime;
            Task? runner = _runner;
            _lifetime = null;
            _runner = null;
            if (lifetime is null)
            {
                return;
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            if (runner is null)
            {
                lifetime.Dispose();
                return;
            }

            try
            {
                await runner.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || runner.IsCanceled)
            {
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                TimeSpan nextDelay = _interval;
                if (_runImmediately)
                {
                    bool transientFailure = await RunSyncAsync(cancellationToken).ConfigureAwait(false);
                    nextDelay = transientFailure ? _connectionRetryInterval : _interval;
                }

                while (true)
                {
                    await _delayAsync(nextDelay, cancellationToken).ConfigureAwait(false);
                    bool transientFailure = await RunSyncAsync(cancellationToken).ConfigureAwait(false);
                    nextDelay = transientFailure ? _connectionRetryInterval : _interval;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task<bool> RunSyncAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Requesting periodic change-feed check.");
                await _supervisor
                    .SyncAllAsync(SyncRunRequest.ForFull(SyncRunCause.Periodic), cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                bool transientFailure = SyncFailureClassifier.IsTransientConnectionFailure(exception);
                if (transientFailure)
                {
                    _logger.LogWarning(
                        exception,
                        "Periodic change-feed check could not reach Cotton Cloud; retrying after {RetryInterval}.",
                        _connectionRetryInterval);
                }
                else
                {
                    _logger.LogError(exception, "Periodic change-feed check failed.");
                }

                return transientFailure;
            }
        }
    }
}
