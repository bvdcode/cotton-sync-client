// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private bool CanApplySessionEvents => !_isDisposed && IsSignedIn;

        private bool IsCurrentSessionRevision(int revision)
        {
            return !_isDisposed && revision == Volatile.Read(ref _sessionEventRevision);
        }

        private LinkedListNode<Action> QueueSyncEventUnsafe(Action action, LinkedListNode<Action>? previous)
        {
            if (previous?.List == _pendingSyncEvents)
            {
                _pendingSyncEvents.Remove(previous);
                previous.Value = CreateSessionEventAction(action);
                _pendingSyncEvents.AddLast(previous);
                return previous;
            }

            return _pendingSyncEvents.AddLast(CreateSessionEventAction(action));
        }

        private bool ScheduleSyncEventDispatchUnsafe()
        {
            if (_isSyncEventDispatchQueued)
            {
                return false;
            }

            _isSyncEventDispatchQueued = true;
            return true;
        }

        private void ApplyPendingSyncEvents()
        {
            Action[] actions;
            lock (_progressDispatchGate)
            {
                actions = _pendingSyncEvents.ToArray();
                ClearPendingSyncEventsUnsafe();
                _isSyncEventDispatchQueued = false;
            }

            foreach (Action action in actions)
            {
                action();
            }
        }

        private Action CreateSessionEventAction(Action action)
        {
            int revision = Volatile.Read(ref _sessionEventRevision);
            return () =>
            {
                if (IsCurrentSessionRevision(revision))
                {
                    action();
                }
            };
        }

        private void InvalidatePendingSessionEvents()
        {
            Interlocked.Increment(ref _sessionEventRevision);
            lock (_progressDispatchGate)
            {
                ClearPendingSyncEventsUnsafe();
            }

            lock (_activityDispatchGate)
            {
                _pendingCoalescedActivity = null;
                _isCoalescedActivityDispatchScheduled = false;
            }

            _latestRunProgressByPair.Clear();
            _suppressedInitialSyncCompleteUntilRunProgressCompleted.Clear();
        }

        private void ClearPendingSyncEventsUnsafe()
        {
            _pendingSyncEvents.Clear();
            _pendingStatusEvent = null;
            _pendingTransferEvent = null;
            _pendingRunEvent = null;
            _pendingCoalescedTransferProgress = null;
            _pendingCoalescedRunProgress = null;
        }

        private bool CanApplySyncProgress(SyncPairRowViewModel syncPair)
        {
            return CanApplySessionEvents
                && syncPair.IsEnabled
                && !IsSyncPausePending
                && !string.Equals(syncPair.Status, "Paused", StringComparison.Ordinal)
                && !string.Equals(syncPair.Status, "Pausing", StringComparison.Ordinal);
        }
    }
}
