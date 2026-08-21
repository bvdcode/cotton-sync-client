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
        public string BrowserSignInStatus
        {
            get => _browserSignInStatus;
            private set
            {
                if (SetProperty(ref _browserSignInStatus, value))
                {
                    OnPropertyChanged(nameof(HasBrowserSignInStatus));
                }
            }
        }

        public bool HasBrowserSignInStatus => !string.IsNullOrWhiteSpace(BrowserSignInStatus);

        public string BrowserSignInButtonText => IsBrowserSignInPending
            ? "Waiting for approval"
            : "Open browser";

        public bool IsPasswordSignInVisible => !IsBrowserSignInPending;

        public bool IsSignedIn
        {
            get => _isSignedIn;
            private set
            {
                if (SetProperty(ref _isSignedIn, value))
                {
                    OnPropertyChanged(nameof(IsDashboardVisible));
                    OnPropertyChanged(nameof(IsDashboardHeaderVisible));
                    OnPropertyChanged(nameof(IsSetupVisible));
                    OnPropertyChanged(nameof(HeaderTitleText));
                    RaiseSetupStateProperties();
                    OnPropertyChanged(nameof(CanRetryActionRequired));
                    RefreshCurrentProgressText();
                    RaiseCommandStates();
                }
            }
        }

        public bool HasStoredSession
        {
            get => _hasStoredSession;
            private set
            {
                if (SetProperty(ref _hasStoredSession, value))
                {
                    OnPropertyChanged(nameof(IsStoredSessionRestoreVisible));
                    RetryStoredSessionCommand.RaiseCanExecuteChanged();
                    RaiseSetupStateProperties();
                    RefreshCurrentProgressText();
                }
            }
        }

        public string StoredSessionRestoreMessage
        {
            get => _storedSessionRestoreMessage;
            private set
            {
                if (SetProperty(ref _storedSessionRestoreMessage, value))
                {
                    OnPropertyChanged(nameof(HasStoredSessionRestoreMessage));
                }
            }
        }

        public bool HasStoredSessionRestoreMessage => !string.IsNullOrWhiteSpace(StoredSessionRestoreMessage);

        public bool HasNoSyncPairs => SyncPairs.Count == 0;

        public bool HasNoActivities => Activities.Count == 0;

        public bool HasActivities => Activities.Count > 0;

        public bool IsActivityVisible
        {
            get => _isActivityVisible;
            private set
            {
                if (SetProperty(ref _isActivityVisible, value))
                {
                    OnPropertyChanged(nameof(IsActivityHidden));
                    OnPropertyChanged(nameof(ActivityToggleToolTip));
                }
            }
        }

        public bool IsActivityHidden => !IsActivityVisible;

        public string ActivityToggleToolTip => IsActivityVisible ? "Hide activity" : "Show activity";

        public bool HasConflicts => Conflicts.Count > 0;

        public string ConflictCountLabel => Conflicts.Count == 1 ? "1 conflict" : Conflicts.Count + " conflicts";

        public bool HasActionRequired => !string.IsNullOrWhiteSpace(ActionRequiredMessage);

        public bool HasStatusAttention => HasActionRequired || HasConflicts || HasPairStatusAttention;

        private bool HasOfflineSyncPairs => SyncPairs.Any(static pair => pair.IsEnabled
            && string.Equals(pair.Status, "Offline", StringComparison.Ordinal));

        public bool HasOfflineStatus => HasOfflineSyncPairs && !HasActionRequired && !HasConflicts;

        public bool HasWaitingStatus => string.Equals(GlobalStatus, "Waiting", StringComparison.Ordinal);

        private bool HasPairStatusAttention => SyncPairs.Any(static pair => pair.IsStatusAttention);

        public bool IsStatusCardVisible =>
            HasSyncPairs
            && !HasActionRequired
            && !HasConflicts
            && !HasCurrentWorkProgress
            && (IsExportingDiagnostics || !HasHealthySyncedIdleState);

        public bool IsDashboardChromeVisible => !IsAddSyncPairWizardVisible && !IsSettingsVisible;

        public bool IsDashboardHeaderVisible => IsDashboardVisible && !IsAddSyncPairWizardVisible && !IsSettingsVisible;

        public double ActionRequiredOpacity => HasActionRequired ? 1 : 0;

        public bool CanRetryActionRequired => HasActionRequired
            && IsSignedIn
            && !HasRemoteMassDeleteGuard();

        public bool CanApproveRemoteMassDelete => HasActionRequired
            && IsSignedIn
            && !IsBusy
            && TryResolveRemoteMassDeleteApproval(out _, out _);

        public string RemoteMassDeleteApprovalText => TryResolveRemoteMassDeleteApproval(
            out _,
            out RemoteDeletePlanApproval approval)
            ? "Approve " + approval.DeleteCount.ToString("N0", CultureInfo.InvariantCulture) + " deletes"
            : "Approve deletes";

        public string RemoteMassDeleteApprovalToolTip => TryResolveRemoteMassDeleteApproval(
            out _,
            out RemoteDeletePlanApproval approval)
            ? "Approve deletion of exactly " + approval.DeleteCount.ToString("N0", CultureInfo.InvariantCulture) + " cloud files"
            : "Approve the blocked cloud delete plan";

        public bool HasNoRemoteFolders => RemoteFolders.Count == 0;

        public bool HasRemoteFolders => RemoteFolders.Count > 0;

        public bool HasNoSelfTestItems => SelfTestItems.Count == 0;

        public bool HasSelfTestItems => SelfTestItems.Count > 0;

        public bool HasNotifications => Notifications.Count > 0;

        public bool HasDashboardNotifications =>
            Notifications.Any(static notification => notification.IsDashboardVisible)
            && !HasStatusAttention
            && !IsStatusCardVisible
            && !HasCurrentWorkProgress;

        public bool HasSyncPairs => SyncPairs.Count > 0;

        public bool IsSelectedSyncPairEditorVisible
        {
            get => _isSelectedSyncPairEditorVisible;
            private set
            {
                if (SetProperty(ref _isSelectedSyncPairEditorVisible, value))
                {
                    UpdateSelectedSyncPairEditorVisibility();
                    CancelSelectedSyncPairEditorCommand.RaiseCanExecuteChanged();
                    RemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanSyncNow => IsSignedIn && !IsBusy && HasEnabledSyncPairs && !IsSyncPaused && !IsSyncPausePending;

        public bool CanPauseSync => IsSignedIn && HasEnabledSyncPairs && !IsSyncPaused && !IsSyncPausePending;

        public bool CanResumeSync => IsSignedIn && IsSyncPaused;

        public bool CanTogglePauseResumeSync => CanPauseSync || CanResumeSync;

        public bool CanShowPauseResumeTrayAction => IsSignedIn && HasEnabledSyncPairs;

        public string PauseResumeSyncLabel => IsSyncPausePending ? "Pausing sync" : IsSyncPaused ? "Resume sync" : "Pause sync";

        public string PauseResumeTrayLabel => IsSyncPausePending ? "Pausing" : IsSyncPaused ? "Resume" : "Pause";

        public bool CanOpenTrayFolder => IsSignedIn && !IsBusy && SyncPairs.Count == 1;

        public string TrayOpenFolderLabel => "Open local folder";

        public bool IsSyncPaused => HasEnabledSyncPairs
            && SyncPairs
                .Where(static syncPair => syncPair.IsEnabled)
                .All(static syncPair => string.Equals(syncPair.Status, "Paused", StringComparison.Ordinal));

        public bool IsSyncPausePending
        {
            get => _isSyncPausePending;
            private set
            {
                if (SetProperty(ref _isSyncPausePending, value))
                {
                    RaiseSyncStateProperties();
                }
            }
        }

        private bool HasEnabledSyncPairs => SyncPairs.Any(static syncPair => syncPair.IsEnabled);

        private bool HasHealthySyncedIdleState
        {
            get
            {
                bool hasEnabledPair = false;
                foreach (SyncPairRowViewModel syncPair in SyncPairs)
                {
                    if (!syncPair.IsEnabled)
                    {
                        continue;
                    }

                    hasEnabledPair = true;
                    if (!syncPair.LastSyncedAtUtc.HasValue
                        || !string.Equals(syncPair.Status, "Idle", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return hasEnabledPair;
            }
        }

        public bool IsStartupLoadingVisible => _isLoadingSnapshot;

        public bool IsDashboardVisible => IsSignedIn && !IsStartupLoadingVisible;

        public bool IsSetupVisible => !IsSignedIn && !IsStartupLoadingVisible;

        public bool IsStoredSessionRestoreVisible => IsSetupVisible && HasStoredSession;

        public bool IsServerStepVisible => IsSetupVisible && !HasStoredSession && !IsServerVerified;

        public bool IsSignInStepVisible => IsSetupVisible && !HasStoredSession && IsServerVerified;

        public string SetupTitle => HasStoredSession
            ? "Reconnecting Cotton Sync"
            : IsServerVerified ? "Sign in" : "Connect Cotton Sync";

        public string SetupSubtitle => HasStoredSession
            ? "Your saved session is waiting for Cotton Cloud."
            : IsServerVerified
                ? "Use your Cotton Cloud account."
                : "Choose the Cotton Cloud server for this computer.";

        public bool StartWithOperatingSystem
        {
            get => _startWithOperatingSystem;
            set
            {
                if (value && !IsStartWithOperatingSystemSupported)
                {
                    return;
                }

                if (SetProperty(ref _startWithOperatingSystem, value) && !_isLoadingSnapshot)
                {
                    _ = ApplyStartWithOperatingSystemAsync(value);
                }
            }
        }

        public bool EnableNotifications
        {
            get => _enableNotifications;
            set
            {
                if (SetProperty(ref _enableNotifications, value) && !_isLoadingSnapshot)
                {
                    _ = ApplyNotificationsEnabledAsync(value);
                }
            }
        }

        public int ThemeModeIndex
        {
            get => (int)_themeMode;
            set
            {
                AppThemeMode themeMode = NormalizeThemeModeIndex(value);
                if (_themeMode == themeMode)
                {
                    return;
                }

                AppThemeMode previousThemeMode = _themeMode;
                _themeMode = themeMode;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeModeLabel));
                _themeService.Apply(themeMode);
                if (!_isLoadingSnapshot)
                {
                    _ = ApplyThemeModeAsync(themeMode, previousThemeMode);
                }
            }
        }

        public string ThemeModeLabel => _themeMode switch
        {
            AppThemeMode.System => "System",
            AppThemeMode.Light => "Light",
            AppThemeMode.Dark => "Dark",
            _ => "System",
        };

        public bool IsStartWithOperatingSystemSupported
        {
            get => _isStartWithOperatingSystemSupported;
            private set
            {
                if (SetProperty(ref _isStartWithOperatingSystemSupported, value))
                {
                    OnPropertyChanged(nameof(AutostartStatusText));
                }
            }
        }

        public bool IsTrayLifecycleSupported
        {
            get => _isTrayLifecycleSupported;
            private set
            {
                if (SetProperty(ref _isTrayLifecycleSupported, value))
                {
                    OnPropertyChanged(nameof(IsTrayLifecycleUnsupported));
                    OnPropertyChanged(nameof(AutostartStatusText));
                    OnPropertyChanged(nameof(TrayLifecycleStatusText));
                }
            }
        }

        public bool IsTrayLifecycleUnsupported => !IsTrayLifecycleSupported;

        public string TrayLifecycleDetails
        {
            get => _trayLifecycleDetails;
            private set
            {
                if (SetProperty(ref _trayLifecycleDetails, value))
                {
                    OnPropertyChanged(nameof(AutostartStatusText));
                    OnPropertyChanged(nameof(TrayLifecycleStatusText));
                }
            }
        }

        public string AutostartStatusText
        {
            get
            {
                if (!IsStartWithOperatingSystemSupported)
                {
                    return "Autostart is not available for this launch. Publish or install Cotton Sync to enable startup registration.";
                }

                return IsTrayLifecycleSupported
                    ? "Cotton Sync can start minimized and keep running in the tray."
                    : "Cotton Sync can start with your desktop session and opens as a normal window on this platform.";
            }
        }
    }
}
