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
        private static void EnsureSyncPairProgress(SyncPairRowViewModel row)
        {
            if (row.HasCurrentProgress)
            {
                return;
            }

            row.HasCurrentProgress = true;
            row.IsCurrentProgressIndeterminate = true;
            row.CurrentProgressValue = 0;
        }

        private static void ClearSyncPairProgress(SyncPairRowViewModel row)
        {
            row.CurrentOperation = string.Empty;
            row.HasCurrentProgress = false;
            row.IsCurrentProgressIndeterminate = false;
            row.CurrentProgressValue = 0;
        }

        private void AddNotifications(IReadOnlyList<DesktopNotificationRequest> requests)
        {
            foreach (DesktopNotificationRequest request in requests)
            {
                if (Notifications.FirstOrDefault() is { } latest
                    && string.Equals(latest.Title, request.Title, StringComparison.Ordinal)
                    && string.Equals(latest.Message, request.Message, StringComparison.Ordinal))
                {
                    continue;
                }

                Notifications.Insert(0, new NotificationRowViewModel
                {
                    Title = request.Title,
                    Message = request.Message,
                    IsDashboardVisible = IsDashboardNotificationKind(request.Kind),
                });
                AddActivity("Notification", string.Empty, request.Message);
                if (EnableNotifications && _notificationService.IsSupported)
                {
                    _notificationService.Show(request.Title, request.Message);
                }
            }

            while (Notifications.Count > 3)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }
        }

        private static bool IsDashboardNotificationKind(DesktopNotificationKind kind)
        {
            return kind != DesktopNotificationKind.InitialSyncComplete
                && kind != DesktopNotificationKind.ActionRequiredError;
        }

        private void ShowNativeNotification(string title, string message)
        {
            if (EnableNotifications && _notificationService.IsSupported)
            {
                _notificationService.Show(
                    DesktopUserMessageFormatter.Compact(title, DesktopUserMessageFormatter.TitleMaxLength),
                    DesktopUserMessageFormatter.Compact(message));
            }
        }

        private string ResolveGlobalStatus(DesktopSyncStatusSnapshot status)
        {
            if (!IsSignedIn)
            {
                return "Signed out";
            }

            if (status.SyncPairs.Any(static pair => string.Equals(pair.Status, "Error", StringComparison.Ordinal)))
            {
                return "Action required";
            }

            if (status.SyncPairs.Any(static pair => string.Equals(pair.Status, "Syncing", StringComparison.Ordinal)
                || string.Equals(pair.Status, "Scanning", StringComparison.Ordinal)))
            {
                return "Syncing";
            }

            if (status.SyncPairs.Any(static pair => string.Equals(pair.Status, "Waiting", StringComparison.Ordinal)))
            {
                return "Waiting";
            }

            if (status.SyncPairs.Any(static pair => string.Equals(pair.Status, "Offline", StringComparison.Ordinal)))
            {
                return "Offline";
            }

            IEnumerable<DesktopSyncPairStatusSnapshot> enabledPairs = status.SyncPairs
                .Where(static pair => !string.Equals(pair.Status, "Disabled", StringComparison.Ordinal));
            if (enabledPairs.Any()
                && enabledPairs.All(static pair => string.Equals(pair.Status, "Paused", StringComparison.Ordinal)))
            {
                return "Paused";
            }

            return "Connected";
        }

        private void AddActivity(string kind, string path, string details)
        {
            AddActivity(kind, path, details, DateTimeOffset.Now);
        }

        private void AddActivity(string kind, string path, string details, DateTimeOffset occurredAt, Guid? syncPairId = null)
        {
            ActivityRowViewModel row = CreateActivityRow(kind, path, details, occurredAt);
            if (ShouldCoalesceActivity(kind, syncPairId, occurredAt))
            {
                Activities[0] = row;
                _lastCoalescedActivityAt = occurredAt;
                return;
            }

            Activities.Insert(0, row);
            TrackCoalescibleActivity(kind, syncPairId, occurredAt);
            while (Activities.Count > MaxActivityRows)
            {
                Activities.RemoveAt(Activities.Count - 1);
            }
        }

        private static ActivityRowViewModel CreateActivityRow(string kind, string path, string details, DateTimeOffset occurredAt)
        {
            return new ActivityRowViewModel
            {
                Time = occurredAt.ToString("HH:mm", CultureInfo.CurrentCulture),
                Kind = kind,
                Path = path,
                Details = string.IsNullOrWhiteSpace(details)
                    ? string.Empty
                    : DesktopUserMessageFormatter.Compact(details),
            };
        }

        private bool ShouldCoalesceActivity(string kind, Guid? syncPairId, DateTimeOffset occurredAt)
        {
            if (!IsHighVolumeActivity(kind)
                || Activities.Count == 0
                || !_lastCoalescedActivityAt.HasValue
                || !Equals(_lastCoalescedActivitySyncPairId, syncPairId))
            {
                return false;
            }

            ActivityRowViewModel latest = Activities[0];
            return string.Equals(latest.Kind, kind, StringComparison.Ordinal)
                && occurredAt >= _lastCoalescedActivityAt.Value
                && occurredAt - _lastCoalescedActivityAt.Value <= TransferActivityCoalescingWindow;
        }

        private void TrackCoalescibleActivity(string kind, Guid? syncPairId, DateTimeOffset occurredAt)
        {
            if (!IsHighVolumeActivity(kind))
            {
                _lastCoalescedActivityAt = null;
                _lastCoalescedActivitySyncPairId = null;
                return;
            }

            _lastCoalescedActivityAt = occurredAt;
            _lastCoalescedActivitySyncPairId = syncPairId;
        }

        private static bool IsHighVolumeActivity(string kind)
        {
            return string.Equals(kind, "Uploaded", StringComparison.Ordinal)
                || string.Equals(kind, "Downloaded", StringComparison.Ordinal)
                || string.Equals(kind, "Deleted local copy", StringComparison.Ordinal)
                || string.Equals(kind, "Deleted remote copy", StringComparison.Ordinal)
                || string.Equals(kind, "PlaceholderCreated", StringComparison.Ordinal);
        }

        private void AddConflict(Guid? syncPairId, string path, string details, DateTimeOffset occurredAt)
        {
            if (syncPairId.HasValue
                && SyncPairs.FirstOrDefault(pair => pair.Id == syncPairId.Value) is { } syncPair)
            {
                syncPair.Status = "Conflict";
                syncPair.LastError = details;
            }

            ConflictRowViewModel conflict = new()
            {
                SyncPairId = syncPairId,
                Time = occurredAt.ToString("HH:mm", CultureInfo.CurrentCulture),
                Path = path,
                Details = details,
            };
            Conflicts.Insert(0, conflict);
            SelectedConflict ??= conflict;
            while (Conflicts.Count > MaxConflictRows)
            {
                Conflicts.RemoveAt(Conflicts.Count - 1);
            }

            RaiseSyncStateProperties();
        }

        private void RaiseCommandStates()
        {
            RaiseSyncStateProperties();
            SignInCommand.RaiseCanExecuteChanged();
            SignInWithBrowserCommand.RaiseCanExecuteChanged();
            CancelBrowserSignInCommand.RaiseCanExecuteChanged();
            RetryStoredSessionCommand.RaiseCanExecuteChanged();
            SignOutCommand.RaiseCanExecuteChanged();
            AddSyncPairCommand.RaiseCanExecuteChanged();
            BrowseLocalFolderCommand.RaiseCanExecuteChanged();
            CancelAddSyncPairCommand.RaiseCanExecuteChanged();
            CancelCreateRemoteFolderCommand.RaiseCanExecuteChanged();
            CancelRemoveSyncPairCommand.RaiseCanExecuteChanged();
            ChangeServerCommand.RaiseCanExecuteChanged();
            CreateRemoteFolderCommand.RaiseCanExecuteChanged();
            OpenRemoteFolderCommand.RaiseCanExecuteChanged();
            RemoteFolderUpCommand.RaiseCanExecuteChanged();
            SyncNowCommand.RaiseCanExecuteChanged();
            ApproveRemoteMassDeleteCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
            OpenTrayFolderCommand.RaiseCanExecuteChanged();
            OpenConflictCommand.RaiseCanExecuteChanged();
            ToggleActivityCommand.RaiseCanExecuteChanged();
            ToggleSelectedSyncPairEnabledCommand.RaiseCanExecuteChanged();
            SaveSelectedSyncPairNameCommand.RaiseCanExecuteChanged();
            RemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
            ShowSelectedSyncPairEditorCommand.RaiseCanExecuteChanged();
            ConfirmRemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
            CancelSelectedSyncPairEditorCommand.RaiseCanExecuteChanged();
            OpenWebCommand.RaiseCanExecuteChanged();
            ShowAddSyncPairCommand.RaiseCanExecuteChanged();
            ShowCreateRemoteFolderCommand.RaiseCanExecuteChanged();
            UseRemoteFolderCommand.RaiseCanExecuteChanged();
            ShowSettingsCommand.RaiseCanExecuteChanged();
            CloseSettingsCommand.RaiseCanExecuteChanged();
            SelfTestCommand.RaiseCanExecuteChanged();
            ExportDiagnosticsCommand.RaiseCanExecuteChanged();
            RaiseUpdateCommandStates();
            OpenDataFolderCommand.RaiseCanExecuteChanged();
            OpenDiagnosticsBundleFolderCommand.RaiseCanExecuteChanged();
            RaiseTrayOpenFolderProperties();
        }

        private void RaiseUpdateCommandStates()
        {
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
            DownloadUpdateCommand.RaiseCanExecuteChanged();
            InstallUpdateCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(CanDownloadUpdate));
            OnPropertyChanged(nameof(IsUpdateDownloadVisible));
            OnPropertyChanged(nameof(CanInstallUpdate));
            OnPropertyChanged(nameof(IsUpdateInstallVisible));
        }

        private void RaiseSyncStateProperties()
        {
            SyncNowCommand.RaiseCanExecuteChanged();
            ApproveRemoteMassDeleteCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanSyncNow));
            OnPropertyChanged(nameof(CanApproveRemoteMassDelete));
            OnPropertyChanged(nameof(RemoteMassDeleteApprovalText));
            OnPropertyChanged(nameof(RemoteMassDeleteApprovalToolTip));
            OnPropertyChanged(nameof(CanPauseSync));
            OnPropertyChanged(nameof(CanResumeSync));
            OnPropertyChanged(nameof(CanTogglePauseResumeSync));
            OnPropertyChanged(nameof(CanShowPauseResumeTrayAction));
            OnPropertyChanged(nameof(PauseResumeSyncLabel));
            OnPropertyChanged(nameof(PauseResumeTrayLabel));
            OnPropertyChanged(nameof(PauseResumeCommand));
            OnPropertyChanged(nameof(IsSyncPaused));
            OnPropertyChanged(nameof(HasStatusAttention));
            OnPropertyChanged(nameof(HasOfflineStatus));
            OnPropertyChanged(nameof(HasWaitingStatus));
            OnPropertyChanged(nameof(IsStatusCardVisible));
            OnPropertyChanged(nameof(HeaderStatusText));
            OnPropertyChanged(nameof(StatusCardTitle));
            OnPropertyChanged(nameof(StatusCardDetailText));
            OnPropertyChanged(nameof(HasStatusCardDetail));
            OnPropertyChanged(nameof(HasDashboardNotifications));
        }

        private void RaiseTrayOpenFolderState()
        {
            OpenTrayFolderCommand.RaiseCanExecuteChanged();
            RaiseTrayOpenFolderProperties();
        }

        private void RaiseTrayOpenFolderProperties()
        {
            OnPropertyChanged(nameof(CanOpenTrayFolder));
            OnPropertyChanged(nameof(TrayOpenFolderLabel));
        }

        private void RaiseAddSyncPairFlowCommandStates()
        {
            AddSyncPairCommand.RaiseCanExecuteChanged();
            BrowseLocalFolderCommand.RaiseCanExecuteChanged();
            CancelAddSyncPairCommand.RaiseCanExecuteChanged();
            CancelCreateRemoteFolderCommand.RaiseCanExecuteChanged();
            CreateRemoteFolderCommand.RaiseCanExecuteChanged();
            OpenRemoteFolderCommand.RaiseCanExecuteChanged();
            RemoteFolderUpCommand.RaiseCanExecuteChanged();
            UseRemoteFolderCommand.RaiseCanExecuteChanged();
            ShowAddSyncPairCommand.RaiseCanExecuteChanged();
            ShowCreateRemoteFolderCommand.RaiseCanExecuteChanged();
        }

        private void RaiseSetupStateProperties()
        {
            OnPropertyChanged(nameof(IsStoredSessionRestoreVisible));
            OnPropertyChanged(nameof(IsServerStepVisible));
            OnPropertyChanged(nameof(IsSignInStepVisible));
            OnPropertyChanged(nameof(SetupTitle));
            OnPropertyChanged(nameof(SetupSubtitle));
        }
    }
}
