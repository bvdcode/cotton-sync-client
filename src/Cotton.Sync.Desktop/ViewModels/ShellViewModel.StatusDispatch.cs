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
        private void OnStatusChanged(object? sender, DesktopSyncStatusSnapshot status)
        {
            if (_uiDispatcher.CheckAccess())
            {
                ApplyStatus(status);
                return;
            }

            PostCoalescedStatus(status);
        }

        private void PostCoalescedStatus(DesktopSyncStatusSnapshot status)
        {
            lock (_statusDispatchGate)
            {
                _pendingStatus = status;
                if (_isStatusDispatchQueued)
                {
                    return;
                }

                _isStatusDispatchQueued = true;
            }

            _uiDispatcher.Post(ApplyPendingStatus);
        }

        private void ApplyPendingStatus()
        {
            DesktopSyncStatusSnapshot? status;
            lock (_statusDispatchGate)
            {
                status = _pendingStatus;
                _pendingStatus = null;
                _isStatusDispatchQueued = false;
            }

            if (status is not null)
            {
                ApplyStatus(status);
            }
        }

        private void OnActivityReported(object? sender, DesktopActivitySnapshot activity)
        {
            if (_uiDispatcher.CheckAccess())
            {
                ApplyActivity(activity);
                return;
            }

            if (TryPostCoalescedActivity(activity))
            {
                return;
            }

            _uiDispatcher.Post(() => ApplyActivity(activity));
        }

        private bool TryPostCoalescedActivity(DesktopActivitySnapshot activity)
        {
            if (!IsHighVolumeActivity(activity.Kind))
            {
                return false;
            }

            lock (_activityDispatchGate)
            {
                if (_pendingCoalescedActivity is not null
                    && CanReplacePendingActivity(_pendingCoalescedActivity, activity))
                {
                    _pendingCoalescedActivity = activity;
                    return true;
                }

                if (_isCoalescedActivityDispatchScheduled)
                {
                    return false;
                }

                _pendingCoalescedActivity = activity;
                _isCoalescedActivityDispatchScheduled = true;
            }

            _uiDispatcher.Post(ApplyPendingCoalescedActivity);
            return true;
        }

        private void ApplyPendingCoalescedActivity()
        {
            DesktopActivitySnapshot? activity;
            lock (_activityDispatchGate)
            {
                activity = _pendingCoalescedActivity;
                _pendingCoalescedActivity = null;
                _isCoalescedActivityDispatchScheduled = false;
            }

            if (activity is not null)
            {
                ApplyActivity(activity);
            }
        }

        private static bool CanReplacePendingActivity(
            DesktopActivitySnapshot pending,
            DesktopActivitySnapshot next)
        {
            if (!IsHighVolumeActivity(next.Kind)
                || !string.Equals(pending.Kind, next.Kind, StringComparison.Ordinal)
                || !Equals(pending.SyncPairId, next.SyncPairId)
                || next.OccurredAtUtc < pending.OccurredAtUtc)
            {
                return false;
            }

            return next.OccurredAtUtc - pending.OccurredAtUtc <= TransferActivityCoalescingWindow;
        }

        private void OnSessionRevoked(object? sender, DesktopSessionRevocationSnapshot sessionRevocation)
        {
            if (_uiDispatcher.CheckAccess())
            {
                ApplySessionRevocation(sessionRevocation);
                return;
            }

            _uiDispatcher.Post(() => ApplySessionRevocation(sessionRevocation));
        }

        private void ApplySessionRevocation(DesktopSessionRevocationSnapshot sessionRevocation)
        {
            if (!IsSignedIn)
            {
                return;
            }

            DateTimeOffset occurredAt = new DateTimeOffset(DateTime.SpecifyKind(sessionRevocation.OccurredAtUtc, DateTimeKind.Utc))
                .ToLocalTime();
            ApplySignedOutState("Session expired");
            AddActivity("Account", string.Empty, "Session revoked by server", occurredAt);
            ShowNativeNotification("Session expired", "Sign in again to continue syncing.");
        }

        private void ApplyActivity(DesktopActivitySnapshot activity)
        {
            DateTimeOffset occurredAt = new DateTimeOffset(DateTime.SpecifyKind(activity.OccurredAtUtc, DateTimeKind.Utc))
                .ToLocalTime();
            AddActivity(
                activity.Kind,
                activity.Path,
                activity.Details,
                occurredAt,
                activity.SyncPairId);
            if (string.Equals(activity.Kind, "Conflict", StringComparison.Ordinal))
            {
                AddConflict(activity.SyncPairId, activity.Path, activity.Details, occurredAt);
            }
        }
    }
}
