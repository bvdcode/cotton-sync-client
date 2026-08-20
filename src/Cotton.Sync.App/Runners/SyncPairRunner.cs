// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Manages runtime state and one-shot synchronization requests for one sync pair.
    /// </summary>
    public class SyncPairRunner : ISyncPairRunner
    {
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly ILogger<SyncPairRunner> _logger;
        private readonly SyncPairSettings _syncPair;
        private readonly SyncPairRequestQueue _requestQueue;
        private readonly SyncPairWorkRetryExecutor _retryExecutor;
        private readonly SyncPairStatusController _statusController;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncPairRunner" /> class.
        /// </summary>
        public SyncPairRunner(
            SyncPairSettings syncPair,
            ISyncPairWork work,
            SyncPairRunnerRetryOptions? retryOptions = null,
            ILogger<SyncPairRunner>? logger = null)
        {
            _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
            ArgumentNullException.ThrowIfNull(work);
            SyncPairRunnerRetryOptions normalizedRetryOptions =
                (retryOptions ?? SyncPairRunnerRetryOptions.Default).Normalize();
            _logger = logger ?? NullLogger<SyncPairRunner>.Instance;
            _requestQueue = new SyncPairRequestQueue(isBlocked: !syncPair.IsEnabled);
            _statusController = new SyncPairStatusController(syncPair);
            _retryExecutor = new SyncPairWorkRetryExecutor(
                _syncPair,
                work,
                normalizedRetryOptions,
                _statusController.SetState,
                _logger);
        }

        /// <inheritdoc />
        public Guid SyncPairId => _syncPair.Id;

        /// <inheritdoc />
        public SyncPairStatus Status => _statusController.Status;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _statusController.SetReadyState();
                _requestQueue.SetBlocked(!_syncPair.IsEnabled);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requestQueue.SetBlocked(isBlocked: true, queueIncomingRequests: true);
            _requestQueue.Cancel(ActiveSyncCancellationReason.Pause);
            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RestoreSyncRequestBlockFromStatus();
                throw;
            }

            try
            {
                if (Status.State != SyncPairRunState.Disabled)
                {
                    _statusController.SetState(SyncPairRunState.Paused);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _statusController.SetReadyState();
                _requestQueue.SetBlocked(!_syncPair.IsEnabled);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task SyncNowAsync(CancellationToken cancellationToken = default)
        {
            await SyncNowAsync(SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SyncNowAsync(SyncRunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!_requestQueue.TryStart(request))
            {
                return;
            }

            try
            {
                bool runAgain;
                do
                {
                    SyncRunRequest activeRequest = _requestQueue.GetActiveRequest();
                    await RunSingleSyncAsync(activeRequest, cancellationToken).ConfigureAwait(false);
                    runAgain = _requestQueue.CompletePassOrTakeQueued();
                }
                while (runAgain);
            }
            catch (Exception exception)
            {
                _requestQueue.FinishAfterFailure(exception);
                throw;
            }
        }

        private async Task RunSingleSyncAsync(SyncRunRequest request, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SyncPairRunState currentState = Status.State;
                if (_requestQueue.IsBlocked
                    || !_syncPair.IsEnabled
                    || currentState is SyncPairRunState.Disabled or SyncPairRunState.Paused)
                {
                    return;
                }

                using CancellationTokenSource activeSyncCancellation = new();
                using CancellationTokenSource syncCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    activeSyncCancellation.Token);
                _requestQueue.SetActiveCancellation(activeSyncCancellation);
                try
                {
                    string syncScope = GetLoggedSyncScope(request);
                    _logger.LogInformation(
                        "Starting {SyncScope} sync for {SyncPairId}; causes={SyncCauses}; requested paths={RequestedPathCount}.",
                        syncScope,
                        _syncPair.Id,
                        request.Causes,
                        request.LocalChangedPaths.Count);
                    _statusController.SetState(SyncPairRunState.Syncing);
                    await _retryExecutor.RunAsync(request, syncCancellation.Token).ConfigureAwait(false);
                    _statusController.SetSuccessfulSyncState(request);
                    _logger.LogInformation(
                        "Completed {SyncScope} sync for {SyncPairId}; causes={SyncCauses}; requested paths={RequestedPathCount}.",
                        syncScope,
                        _syncPair.Id,
                        request.Causes,
                        request.LocalChangedPaths.Count);
                }
                catch (Exception exception)
                {
                    ActiveSyncFailureKind failureKind = ClassifyActiveSyncFailure(
                        exception,
                        activeSyncCancellation,
                        syncCancellation.Token,
                        cancellationToken);
                    switch (failureKind)
                    {
                        case ActiveSyncFailureKind.PausedCancellation:
                            HandlePausedCancellation();
                            return;
                        case ActiveSyncFailureKind.PausedSideEffect:
                            HandlePausedSideEffect(exception);
                            return;
                        case ActiveSyncFailureKind.Superseded:
                            HandleSupersededSync(exception);
                            return;
                        case ActiveSyncFailureKind.Canceled:
                            HandleCanceledSync(exception);
                            throw;
                        case ActiveSyncFailureKind.Stopped:
                            HandleStoppedSync(exception);
                            throw new OperationCanceledException(
                                "Sync pair runner was stopped.",
                                exception,
                                activeSyncCancellation.Token);
                        case ActiveSyncFailureKind.Failed:
                            HandleFailedSync(exception);
                            throw;
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(failureKind),
                                failureKind,
                                "Unsupported sync failure kind.");
                    }
                }
                finally
                {
                    _requestQueue.ClearActiveCancellation(activeSyncCancellation);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private ActiveSyncFailureKind ClassifyActiveSyncFailure(
            Exception exception,
            CancellationTokenSource activeSyncCancellation,
            CancellationToken syncCancellation,
            CancellationToken callerCancellation)
        {
            if (exception is OperationCanceledException)
            {
                if (!callerCancellation.IsCancellationRequested
                    && _requestQueue.IsActiveCancellation(activeSyncCancellation, ActiveSyncCancellationReason.Pause))
                {
                    return ActiveSyncFailureKind.PausedCancellation;
                }

                if (!callerCancellation.IsCancellationRequested
                    && _requestQueue.IsActiveCancellation(activeSyncCancellation, ActiveSyncCancellationReason.Superseded))
                {
                    return ActiveSyncFailureKind.Superseded;
                }

                if (syncCancellation.IsCancellationRequested)
                {
                    return ActiveSyncFailureKind.Canceled;
                }
            }

            if (!callerCancellation.IsCancellationRequested
                && IsActiveSyncCancellationSideEffect(
                    exception,
                    activeSyncCancellation,
                    ActiveSyncCancellationReason.Pause))
            {
                return ActiveSyncFailureKind.PausedSideEffect;
            }

            if (!callerCancellation.IsCancellationRequested
                && IsActiveSyncCancellationSideEffect(
                    exception,
                    activeSyncCancellation,
                    ActiveSyncCancellationReason.Superseded))
            {
                return ActiveSyncFailureKind.Superseded;
            }

            if (!callerCancellation.IsCancellationRequested
                && IsActiveSyncCancellationSideEffect(
                    exception,
                    activeSyncCancellation,
                    ActiveSyncCancellationReason.Stop))
            {
                return ActiveSyncFailureKind.Stopped;
            }

            return ActiveSyncFailureKind.Failed;
        }

        private void HandlePausedCancellation()
        {
            _statusController.SetIdleOrActionRequiredState();
            _logger.LogDebug("Sync pair runner was paused for {SyncPairId}.", _syncPair.Id);
        }

        private void HandlePausedSideEffect(Exception exception)
        {
            _statusController.SetIdleOrActionRequiredState();
            _logger.LogDebug(
                exception,
                "Sync pair runner was paused while in-flight work was canceling for {SyncPairId}.",
                _syncPair.Id);
        }

        private void HandleSupersededSync(Exception exception)
        {
            _statusController.SetIdleOrActionRequiredState();
            _logger.LogDebug(
                exception,
                "Background sync was superseded by scoped work for {SyncPairId}.",
                _syncPair.Id);
        }

        private void HandleCanceledSync(Exception exception)
        {
            _statusController.SetIdleOrActionRequiredState();
            _logger.LogDebug(
                exception,
                "Sync pair runner was canceled for {SyncPairId}.",
                _syncPair.Id);
        }

        private void HandleStoppedSync(Exception exception)
        {
            _statusController.SetState(SyncPairRunState.Disabled);
            _logger.LogDebug(
                exception,
                "Sync pair runner was stopped while in-flight work was canceling for {SyncPairId}.",
                _syncPair.Id);
        }

        private void HandleFailedSync(Exception exception)
        {
            SyncPairRunState failureState = SyncPairRunState.Error;
            if (SyncFailureClassifier.IsTransientConnectionFailure(exception))
            {
                failureState = SyncPairRunState.Offline;
            }

            string failureMessage = SyncPairWorkRetryExecutor.CreateFailureMessage(exception);
            if (failureState == SyncPairRunState.Error)
            {
                _statusController.SetActionRequiredState(failureMessage);
            }
            else
            {
                _statusController.SetState(failureState, failureMessage);
            }
            _logger.LogError(
                exception,
                "Sync pair runner failed for {SyncPairId}.",
                _syncPair.Id);
        }

        private string GetLoggedSyncScope(SyncRunRequest request)
        {
            if (!request.IsFull)
            {
                return "scoped";
            }

            const SyncRunCause feedPlannedCauses = SyncRunCause.Periodic
                | SyncRunCause.RealtimeRemoteChange
                | SyncRunCause.Resume;
            const SyncRunCause allowedCauses = feedPlannedCauses | SyncRunCause.LocalChange;
            return _syncPair.Mode == SyncPairMode.WindowsVirtualFiles
                && (request.Causes & feedPlannedCauses) != SyncRunCause.None
                && (request.Causes & ~allowedCauses) == SyncRunCause.None
                    ? "feed-planned"
                    : "full";
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requestQueue.SetBlocked(isBlocked: true);
            _requestQueue.Cancel(ActiveSyncCancellationReason.Stop);
            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RestoreSyncRequestBlockFromStatus();
                throw;
            }

            try
            {
                _statusController.SetState(SyncPairRunState.Disabled);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private bool IsActiveSyncCancellationSideEffect(
            Exception exception,
            CancellationTokenSource activeSyncCancellation,
            ActiveSyncCancellationReason reason)
        {
            return activeSyncCancellation.IsCancellationRequested
                && _requestQueue.IsActiveCancellation(activeSyncCancellation, reason)
                && IsCancellationSideEffect(exception);
        }

        private static bool IsCancellationSideEffect(Exception exception)
        {
            return exception switch
            {
                IOException => true,
                ObjectDisposedException => true,
                TaskCanceledException => true,
                HttpRequestException { InnerException: not null } requestException
                    => IsCancellationSideEffect(requestException.InnerException!),
                _ => false,
            };
        }

        private void RestoreSyncRequestBlockFromStatus()
        {
            SyncPairRunState state = Status.State;
            bool isPaused = state == SyncPairRunState.Paused;
            _requestQueue.SetBlocked(
                !_syncPair.IsEnabled || state == SyncPairRunState.Disabled || isPaused,
                queueIncomingRequests: isPaused);
        }

    }
}
