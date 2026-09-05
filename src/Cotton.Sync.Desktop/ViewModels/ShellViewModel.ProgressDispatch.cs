// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        private void OnTransferProgressChanged(object? sender, DesktopTransferProgressSnapshot progress)
        {
            if (_uiDispatcher.CheckAccess())
            {
                ApplyPendingSyncEvents();
                if (ShouldQueueVisibleTransferProgress(progress))
                {
                    ApplyTransferProgress(progress);
                }

                return;
            }

            PostCoalescedTransferProgress(progress);
        }

        private void OnRunProgressChanged(object? sender, DesktopRunProgressSnapshot progress)
        {
            if (_uiDispatcher.CheckAccess())
            {
                ApplyPendingSyncEvents();
                if (ShouldQueueVisibleRunProgress(progress))
                {
                    ApplyRunProgress(progress);
                }

                return;
            }

            PostCoalescedRunProgress(progress);
        }

        private void PostCoalescedTransferProgress(DesktopTransferProgressSnapshot progress)
        {
            bool schedule;
            lock (_progressDispatchGate)
            {
                bool replace = _pendingCoalescedTransferProgress is not null
                    && CanReplacePendingTransferProgress(_pendingCoalescedTransferProgress, progress);
                if (replace)
                {
                    TrackVisibleTransferProgressUnsafe(progress);
                }
                else if (!ShouldQueueVisibleTransferProgressUnsafe(progress))
                {
                    return;
                }

                _pendingCoalescedTransferProgress = progress;
                _pendingTransferEvent = QueueSyncEventUnsafe(
                    () => ApplyTransferProgress(progress),
                    replace ? _pendingTransferEvent : null);
                schedule = ScheduleSyncEventDispatchUnsafe();
            }

            if (schedule)
            {
                _uiDispatcher.Post(ApplyPendingSyncEvents);
            }
        }

        private void PostCoalescedRunProgress(DesktopRunProgressSnapshot progress)
        {
            bool schedule;
            lock (_progressDispatchGate)
            {
                bool replace = _pendingCoalescedRunProgress is not null
                    && CanReplacePendingRunProgress(_pendingCoalescedRunProgress, progress);
                if (replace)
                {
                    TrackVisibleRunProgressUnsafe(progress);
                }
                else if (!ShouldQueueVisibleRunProgressUnsafe(progress))
                {
                    return;
                }

                _pendingCoalescedRunProgress = progress;
                _pendingRunEvent = QueueSyncEventUnsafe(
                    () => ApplyRunProgress(progress),
                    replace ? _pendingRunEvent : null);
                schedule = ScheduleSyncEventDispatchUnsafe();
            }

            if (schedule)
            {
                _uiDispatcher.Post(ApplyPendingSyncEvents);
            }
        }

        private static bool CanReplacePendingTransferProgress(
            DesktopTransferProgressSnapshot pending,
            DesktopTransferProgressSnapshot next)
        {
            return pending.SyncPairId == next.SyncPairId
                && pending.Direction == next.Direction
                && string.Equals(pending.RelativePath, next.RelativePath, StringComparison.Ordinal)
                && (!pending.IsCompleted || next.IsCompleted)
                && next.OccurredAtUtc >= pending.OccurredAtUtc;
        }

        private bool ShouldQueueVisibleTransferProgress(DesktopTransferProgressSnapshot progress)
        {
            lock (_progressDispatchGate)
            {
                return ShouldQueueVisibleTransferProgressUnsafe(progress);
            }
        }

        private bool ShouldQueueVisibleTransferProgressUnsafe(DesktopTransferProgressSnapshot progress)
        {
            DateTime occurredAtUtc = progress.OccurredAtUtc.ToUniversalTime();
            bool isNewVisibleTransfer = !_visibleTransferSyncPairId.HasValue
                || _visibleTransferSyncPairId.Value != progress.SyncPairId
                || _visibleTransferDirection != progress.Direction
                || !string.Equals(_visibleTransferRelativePath, progress.RelativePath, StringComparison.Ordinal);
            if (isNewVisibleTransfer
                || progress.IsCompleted
                || !_lastVisibleTransferProgressAtUtc.HasValue
                || occurredAtUtc < _lastVisibleTransferProgressAtUtc.Value
                || occurredAtUtc - _lastVisibleTransferProgressAtUtc.Value >= VisibleTransferProgressUpdateInterval)
            {
                TrackVisibleTransferProgressUnsafe(progress);
                return true;
            }

            return false;
        }

        private void TrackVisibleTransferProgressUnsafe(DesktopTransferProgressSnapshot progress)
        {
            _lastVisibleTransferProgressAtUtc = progress.OccurredAtUtc.ToUniversalTime();
            _visibleTransferSyncPairId = progress.SyncPairId;
            _visibleTransferDirection = progress.Direction;
            _visibleTransferRelativePath = progress.RelativePath;
        }

        private static bool CanReplacePendingRunProgress(
            DesktopRunProgressSnapshot pending,
            DesktopRunProgressSnapshot next)
        {
            return pending.SyncPairId == next.SyncPairId
                && pending.Stage == next.Stage
                && next.StartedAtUtc == pending.StartedAtUtc
                && (!pending.IsCompleted || next.IsCompleted)
                && next.OccurredAtUtc >= pending.OccurredAtUtc;
        }

        private bool ShouldQueueVisibleRunProgress(DesktopRunProgressSnapshot progress)
        {
            lock (_progressDispatchGate)
            {
                return ShouldQueueVisibleRunProgressUnsafe(progress);
            }
        }

        private bool ShouldQueueVisibleRunProgressUnsafe(DesktopRunProgressSnapshot progress)
        {
            DateTime occurredAtUtc = progress.OccurredAtUtc.ToUniversalTime();
            bool isNewVisibleRunProgress = !_visibleRunProgressSyncPairId.HasValue
                || _visibleRunProgressSyncPairId.Value != progress.SyncPairId
                || _visibleRunProgressStage != progress.Stage;
            if (isNewVisibleRunProgress
                || progress.IsCompleted
                || !_lastVisibleRunProgressAtUtc.HasValue
                || occurredAtUtc < _lastVisibleRunProgressAtUtc.Value
                || occurredAtUtc - _lastVisibleRunProgressAtUtc.Value >= VisibleRunProgressUpdateInterval)
            {
                TrackVisibleRunProgressUnsafe(progress);
                return true;
            }

            return false;
        }

        private void TrackVisibleRunProgressUnsafe(DesktopRunProgressSnapshot progress)
        {
            _lastVisibleRunProgressAtUtc = progress.OccurredAtUtc.ToUniversalTime();
            _visibleRunProgressSyncPairId = progress.SyncPairId;
            _visibleRunProgressStage = progress.Stage;
        }
    }
}
