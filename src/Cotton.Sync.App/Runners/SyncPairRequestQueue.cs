// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.Remote;

namespace Cotton.Sync.App.Runners
{
    internal class SyncPairRequestQueue
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _activeCancellation;
        private ActiveSyncCancellationReason _activeCancellationReason;
        private SyncRunRequest? _actionRequiredRequest;
        private SyncRunRequest? _activeRequest;
        private SyncRunRequest? _failedRequest;
        private bool _isBlocked;
        private bool _isSyncInProgress;
        private SyncRunRequest? _pendingFullRequest;
        private SyncRunRequest? _pendingScopedRequest;
        private bool _queueWhileBlocked;

        public SyncPairRequestQueue(bool isBlocked)
        {
            _isBlocked = isBlocked;
        }

        public bool IsBlocked
        {
            get
            {
                lock (_gate)
                {
                    return _isBlocked;
                }
            }
        }

        public bool TryStart(SyncRunRequest request)
        {
            lock (_gate)
            {
                if (_isBlocked)
                {
                    if (_queueWhileBlocked)
                    {
                        QueuePending(request);
                    }

                    return false;
                }

                if (_isSyncInProgress)
                {
                    QueuePending(request);
                    PreemptBackgroundFullSyncIfRequired();
                    return false;
                }

                _isSyncInProgress = true;
                if (request.ApprovedRemoteDeletePlan is not null && _actionRequiredRequest is not null)
                {
                    _activeRequest = _actionRequiredRequest.WithApprovedRemoteDeletePlan(
                        request.ApprovedRemoteDeletePlan);
                    _actionRequiredRequest = null;
                    _failedRequest = null;
                    return true;
                }

                if (_failedRequest is null)
                {
                    _activeRequest = request;
                    return true;
                }

                bool mergeRequest = _failedRequest.IsFull && request.IsFull;
                _activeRequest = mergeRequest ? _failedRequest.Merge(request) : _failedRequest;
                _failedRequest = null;
                if (!mergeRequest)
                {
                    QueuePending(request);
                }

                return true;
            }
        }

        public SyncRunRequest GetActiveRequest()
        {
            lock (_gate)
            {
                return _activeRequest
                    ?? throw new InvalidOperationException("A running sync loop must have an active request.");
            }
        }

        public bool CompletePassOrTakeQueued()
        {
            lock (_gate)
            {
                _actionRequiredRequest = null;
                if (_isBlocked)
                {
                    FinishLoop();
                    return false;
                }

                SyncRunRequest? nextRequest = TakeNextPending();
                if (nextRequest is not null)
                {
                    _activeRequest = nextRequest;
                    return true;
                }

                FinishLoop();
                return false;
            }
        }

        public void FinishAfterFailure(Exception exception)
        {
            lock (_gate)
            {
                _actionRequiredRequest = exception is SyncActionRequiredException
                    ? _activeRequest
                    : null;
                _failedRequest = SyncFailureClassifier.IsTransientConnectionFailure(exception)
                    ? _activeRequest
                    : null;
                if (_failedRequest?.IsFull == true && _pendingFullRequest is not null)
                {
                    _failedRequest = MergePendingFullRequests(_failedRequest, _pendingFullRequest);
                    _pendingFullRequest = null;
                }

                FinishLoop();
            }
        }

        public void Cancel(ActiveSyncCancellationReason reason)
        {
            lock (_gate)
            {
                CancelCore(reason);
            }
        }

        public void SetActiveCancellation(CancellationTokenSource cancellation)
        {
            lock (_gate)
            {
                _activeCancellation = cancellation;
                PreemptBackgroundFullSyncIfRequired();
            }
        }

        public void ClearActiveCancellation(CancellationTokenSource cancellation)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, cancellation))
                {
                    _activeCancellation = null;
                    _activeCancellationReason = ActiveSyncCancellationReason.None;
                }
            }
        }

        public bool IsActiveCancellation(
            CancellationTokenSource cancellation,
            ActiveSyncCancellationReason reason)
        {
            lock (_gate)
            {
                return ReferenceEquals(_activeCancellation, cancellation)
                    && _activeCancellationReason == reason;
            }
        }

        public void SetBlocked(bool isBlocked, bool queueIncomingRequests = false)
        {
            lock (_gate)
            {
                _isBlocked = isBlocked;
                _queueWhileBlocked = isBlocked && queueIncomingRequests;
                if (isBlocked)
                {
                    _actionRequiredRequest = null;
                    _failedRequest = null;
                    _pendingFullRequest = null;
                    _pendingScopedRequest = null;
                }
            }
        }

        private void QueuePending(SyncRunRequest request)
        {
            if (_pendingFullRequest is not null)
            {
                _pendingFullRequest = MergePendingFullRequests(_pendingFullRequest, request);
                return;
            }

            if (request.IsFull)
            {
                _pendingFullRequest = _pendingScopedRequest is null
                    ? ToPendingFullRequest(request)
                    : MergePendingFullRequests(request, _pendingScopedRequest);
                _pendingScopedRequest = null;
                return;
            }

            SyncRunRequest scopedRequest = _pendingScopedRequest is null
                ? request
                : _pendingScopedRequest.Merge(request);
            if (scopedRequest.LocalChangedPaths.Count > SyncRunRequest.MaximumQueuedScopedPaths)
            {
                _pendingFullRequest = SyncRunRequest.ForFull(
                    scopedRequest.Causes | SyncRunCause.LocalChangeOverflow);
                _pendingScopedRequest = null;
                return;
            }

            _pendingScopedRequest = scopedRequest;
        }

        private void PreemptBackgroundFullSyncIfRequired()
        {
            const SyncRunCause backgroundCauses = SyncRunCause.Periodic
                | SyncRunCause.RealtimeRemoteChange
                | SyncRunCause.Resume;
            if (_pendingScopedRequest is not null
                && _activeRequest is { IsFull: true } activeRequest
                && (activeRequest.Causes & ~backgroundCauses) == SyncRunCause.None)
            {
                CancelCore(ActiveSyncCancellationReason.Superseded);
            }
        }

        private void CancelCore(ActiveSyncCancellationReason reason)
        {
            if (_activeCancellation is null)
            {
                return;
            }

            _activeCancellationReason = reason;
            _activeCancellation.Cancel();
        }

        private SyncRunRequest? TakeNextPending()
        {
            if (_pendingFullRequest is not null)
            {
                SyncRunRequest request = _pendingScopedRequest is null
                    ? _pendingFullRequest
                    : _pendingFullRequest.Merge(_pendingScopedRequest);
                _pendingFullRequest = null;
                _pendingScopedRequest = null;
                return request;
            }

            SyncRunRequest? scopedRequest = _pendingScopedRequest;
            _pendingScopedRequest = null;
            return scopedRequest;
        }

        private void FinishLoop()
        {
            _isSyncInProgress = false;
            _activeRequest = null;
        }

        private static SyncRunRequest MergePendingFullRequests(
            SyncRunRequest fullRequest,
            SyncRunRequest other)
        {
            RemoteDeletePlanApproval? approval = Equals(
                fullRequest.ApprovedRemoteDeletePlan,
                other.ApprovedRemoteDeletePlan)
                    ? fullRequest.ApprovedRemoteDeletePlan
                    : null;
            return SyncRunRequest.ForFull(fullRequest.Causes | other.Causes, approval);
        }

        private static SyncRunRequest ToPendingFullRequest(SyncRunRequest request)
        {
            return SyncRunRequest.ForFull(request.Causes, request.ApprovedRemoteDeletePlan);
        }
    }
}
