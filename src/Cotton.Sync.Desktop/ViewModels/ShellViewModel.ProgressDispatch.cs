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
                if (ShouldQueueVisibleTransferProgress(progress))
                {
                    ApplyTransferProgress(progress);
                }

                return;
            }

            if (TryPostCoalescedTransferProgress(progress))
            {
                return;
            }

            if (ShouldQueueVisibleTransferProgress(progress))
            {
                _uiDispatcher.Post(() => ApplyTransferProgress(progress));
            }
        }

        private void OnRunProgressChanged(object? sender, DesktopRunProgressSnapshot progress)
        {
            if (_uiDispatcher.CheckAccess())
            {
                if (ShouldQueueVisibleRunProgress(progress))
                {
                    ApplyRunProgress(progress);
                }

                return;
            }

            if (TryPostCoalescedRunProgress(progress))
            {
                return;
            }

            if (ShouldQueueVisibleRunProgress(progress))
            {
                _uiDispatcher.Post(() => ApplyRunProgress(progress));
            }
        }

        private bool TryPostCoalescedTransferProgress(DesktopTransferProgressSnapshot progress)
        {
            lock (_progressDispatchGate)
            {
                if (_pendingCoalescedTransferProgress is not null
                    && CanReplacePendingTransferProgress(_pendingCoalescedTransferProgress, progress))
                {
                    _pendingCoalescedTransferProgress = progress;
                    TrackVisibleTransferProgressUnsafe(progress);
                    return true;
                }

                if (_isCoalescedTransferProgressDispatchScheduled)
                {
                    return false;
                }

                if (!ShouldQueueVisibleTransferProgressUnsafe(progress))
                {
                    return true;
                }

                _pendingCoalescedTransferProgress = progress;
                _isCoalescedTransferProgressDispatchScheduled = true;
            }

            _uiDispatcher.Post(ApplyPendingCoalescedTransferProgress);
            return true;
        }

        private bool TryPostCoalescedRunProgress(DesktopRunProgressSnapshot progress)
        {
            lock (_progressDispatchGate)
            {
                if (_pendingCoalescedRunProgress is not null
                    && CanReplacePendingRunProgress(_pendingCoalescedRunProgress, progress))
                {
                    _pendingCoalescedRunProgress = progress;
                    TrackVisibleRunProgressUnsafe(progress);
                    return true;
                }

                if (_isCoalescedRunProgressDispatchScheduled)
                {
                    return false;
                }

                if (!ShouldQueueVisibleRunProgressUnsafe(progress))
                {
                    return true;
                }

                _pendingCoalescedRunProgress = progress;
                _isCoalescedRunProgressDispatchScheduled = true;
            }

            _uiDispatcher.Post(ApplyPendingCoalescedRunProgress);
            return true;
        }

        private void ApplyPendingCoalescedTransferProgress()
        {
            DesktopTransferProgressSnapshot? progress;
            lock (_progressDispatchGate)
            {
                progress = _pendingCoalescedTransferProgress;
                _pendingCoalescedTransferProgress = null;
                _isCoalescedTransferProgressDispatchScheduled = false;
            }

            if (progress is not null)
            {
                ApplyTransferProgress(progress);
            }
        }

        private void ApplyPendingCoalescedRunProgress()
        {
            DesktopRunProgressSnapshot? progress;
            lock (_progressDispatchGate)
            {
                progress = _pendingCoalescedRunProgress;
                _pendingCoalescedRunProgress = null;
                _isCoalescedRunProgressDispatchScheduled = false;
            }

            if (progress is not null)
            {
                ApplyRunProgress(progress);
            }
        }

        private static bool CanReplacePendingTransferProgress(
            DesktopTransferProgressSnapshot pending,
            DesktopTransferProgressSnapshot next)
        {
            return pending.SyncPairId == next.SyncPairId
                && pending.Direction == next.Direction
                && string.Equals(pending.RelativePath, next.RelativePath, StringComparison.Ordinal)
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
