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
        private static T RequireReference<T>(T? value, string parameterName)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);
            return value;
        }

        private static DesktopFeatureFlags ResolveFeatureFlags(DesktopFeatureFlags? featureFlags)
        {
            return featureFlags ?? DesktopFeatureFlags.Default;
        }

        private static TimeSpan ResolvePositiveInterval(
            TimeSpan? configured,
            TimeSpan defaultValue,
            string parameterName)
        {
            TimeSpan value = configured ?? defaultValue;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, parameterName);
            return value;
        }

        private static Func<TimeSpan, CancellationToken, Task> ResolveDelay(
            Func<TimeSpan, CancellationToken, Task>? delay)
        {
            return delay ?? Task.Delay;
        }

        private static IDesktopUiDispatcher ResolveUiDispatcher(IDesktopUiDispatcher? uiDispatcher)
        {
            return uiDispatcher ?? new AvaloniaDesktopUiDispatcher();
        }

        private bool CanChangeServer() => !IsBusy;

        private bool CanCancelAddSyncPair() => !IsBusy && !IsAddingSyncPair;

        private bool CanOpenConflict(object? parameter) => parameter is ConflictRowViewModel && !IsBusy;

        private bool CanEditSelectedSyncPair() => IsSignedIn && SelectedSyncPair is not null && !IsBusy;

        private bool CanRequestRemoveSelectedSyncPair() =>
            CanEditSelectedSyncPair() && !IsRemoveSyncPairConfirmationVisible;

        private bool CanShowSelectedSyncPairEditor(object? parameter) =>
            IsSignedIn && ResolveSyncPairTarget(parameter) is not null && !IsBusy;

        private bool CanCancelSelectedSyncPairEditor() => IsSelectedSyncPairEditorVisible && !IsBusy;

        private bool CanConfirmRemoveSelectedSyncPair() =>
            IsSignedIn && _pendingRemoveSyncPair is not null && !IsBusy;

        private bool CanCancelRemoveSyncPair() => _pendingRemoveSyncPair is not null && !IsBusy;

        private bool CanOpenDataFolder() => HasDataDirectory && !IsBusy;

        private bool CanOpenDiagnosticsBundleFolder() => HasLastDiagnosticsBundlePath && !IsExportingDiagnostics;

        public ObservableCollection<SyncPairRowViewModel> SyncPairs { get; } = [];

        public ObservableCollection<ActivityRowViewModel> Activities { get; } = [];

        public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = [];

        public ObservableCollection<RemoteFolderRowViewModel> RemoteFolders { get; } = [];

        public ObservableCollection<SelfTestItemRowViewModel> SelfTestItems { get; } = [];

        public ObservableCollection<DiagnosticItemRowViewModel> DiagnosticsItems { get; } = [];

        public ObservableCollection<NotificationRowViewModel> Notifications { get; } = [];

        internal event EventHandler? UpdateInstallShutdownRequested;

        public AsyncRelayCommand AddSyncPairCommand { get; }

        public AsyncRelayCommand BrowseLocalFolderCommand { get; }

        public AsyncRelayCommand CancelAddSyncPairCommand { get; }

        public AsyncRelayCommand CancelBrowserSignInCommand { get; }

        public AsyncRelayCommand CancelCreateRemoteFolderCommand { get; }

        public AsyncRelayCommand CancelRemoveSyncPairCommand { get; }

        public AsyncRelayCommand CancelSelectedSyncPairEditorCommand { get; }

        public AsyncRelayCommand ChangeServerCommand { get; }

        public AsyncRelayCommand CloseSettingsCommand { get; }

        public AsyncRelayCommand ConfirmRemoveSelectedSyncPairCommand { get; }

        public AsyncRelayCommand CreateRemoteFolderCommand { get; }

        public AsyncRelayCommand OpenDiagnosticsBundleFolderCommand { get; }

        public AsyncRelayCommand OpenDataFolderCommand { get; }

        public AsyncRelayCommand OpenFolderCommand { get; }

        public AsyncRelayCommand OpenConflictCommand { get; }

        public AsyncRelayCommand OpenTrayFolderCommand { get; }

        public AsyncRelayCommand OpenWebCommand { get; }

        public AsyncRelayCommand ToggleActivityCommand { get; }

        public AsyncRelayCommand OpenRemoteFolderCommand { get; }

        public AsyncRelayCommand RemoveSelectedSyncPairCommand { get; }

        public AsyncRelayCommand SaveSelectedSyncPairNameCommand { get; }

        public AsyncRelayCommand ToggleSelectedSyncPairEnabledCommand { get; }

        public AsyncRelayCommand UseRemoteFolderCommand { get; }

        public AsyncRelayCommand PauseCommand { get; }

        public AsyncRelayCommand PauseResumeCommand { get; }

        public AsyncRelayCommand ResumeCommand { get; }

        public AsyncRelayCommand RemoteFolderUpCommand { get; }

        public AsyncRelayCommand RetryStoredSessionCommand { get; }

        public AsyncRelayCommand SignInCommand { get; }

        public AsyncRelayCommand SignInWithBrowserCommand { get; }

        public AsyncRelayCommand SignOutCommand { get; }

        public AsyncRelayCommand ShowAddSyncPairCommand { get; }

        public AsyncRelayCommand ShowCreateRemoteFolderCommand { get; }

        public AsyncRelayCommand ShowSelectedSyncPairEditorCommand { get; }

        public AsyncRelayCommand ShowSettingsCommand { get; }

        public AsyncRelayCommand SyncNowCommand { get; }

        public AsyncRelayCommand ApproveRemoteMassDeleteCommand { get; }

        public AsyncRelayCommand SelfTestCommand { get; }

        public AsyncRelayCommand ExportDiagnosticsCommand { get; }

        public AsyncRelayCommand CheckForUpdatesCommand { get; }

        public AsyncRelayCommand DownloadUpdateCommand { get; }

        public AsyncRelayCommand InstallUpdateCommand { get; }

        internal Task? StartupUpdateTask => _startupUpdateTask;

        internal Task? PeriodicUpdateTask => _periodicUpdateTask;

        internal Task? StoredSessionRetryTask => _storedSessionRetryCoordinator.RetryTask;

        public string AccountName
        {
            get => _accountName;
            private set
            {
                if (SetProperty(ref _accountName, value))
                {
                    OnPropertyChanged(nameof(HeaderTitleText));
                }
            }
        }

        public string AppVersion => DesktopAppVersion.Current;

        public string UpdateStatusText
        {
            get => _updateStatusText;
            private set => SetProperty(ref _updateStatusText, value);
        }

        public string UpdateDetailsText
        {
            get => _updateDetailsText;
            private set
            {
                if (SetProperty(ref _updateDetailsText, value))
                {
                    OnPropertyChanged(nameof(HasUpdateDetails));
                }
            }
        }

        public bool HasUpdateDetails => !string.IsNullOrWhiteSpace(UpdateDetailsText);

        public bool IsUpdateDownloadProgressVisible
        {
            get => _isUpdateDownloadProgressVisible;
            private set => SetProperty(ref _isUpdateDownloadProgressVisible, value);
        }

        public bool IsUpdateDownloadProgressIndeterminate
        {
            get => _isUpdateDownloadProgressIndeterminate;
            private set => SetProperty(ref _isUpdateDownloadProgressIndeterminate, value);
        }

        public double UpdateDownloadProgressValue
        {
            get => _updateDownloadProgressValue;
            private set => SetProperty(ref _updateDownloadProgressValue, value);
        }

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set
            {
                if (SetProperty(ref _isUpdateAvailable, value))
                {
                    RaiseUpdateCommandStates();
                }
            }
        }

        public bool IsUpdateBusy
        {
            get => _isUpdateBusy;
            private set
            {
                if (SetProperty(ref _isUpdateBusy, value))
                {
                    RaiseUpdateCommandStates();
                }
            }
        }

        public bool IsUpdateReady
        {
            get => _isUpdateReady;
            private set
            {
                if (SetProperty(ref _isUpdateReady, value))
                {
                    RaiseUpdateCommandStates();
                }
            }
        }

        public bool IsUpdateInstallHandoffActive
        {
            get => _isUpdateInstallHandoffActive;
            private set
            {
                if (SetProperty(ref _isUpdateInstallHandoffActive, value))
                {
                    RaiseUpdateCommandStates();
                }
            }
        }

        public bool IsUpdateInstallProgressVisible
        {
            get => _isUpdateInstallProgressVisible;
            private set => SetProperty(ref _isUpdateInstallProgressVisible, value);
        }

        public bool CanCheckForUpdates => !IsUpdateBusy && !IsUpdateInstallHandoffActive;

        public bool CanDownloadUpdate => IsUpdateAvailable && !IsUpdateReady && !IsUpdateBusy && !IsUpdateInstallHandoffActive;

        public bool IsUpdateDownloadVisible => IsUpdateAvailable
            && !IsUpdateReady
            && !IsUpdateBusy
            && !IsUpdateInstallHandoffActive;

        public bool CanInstallUpdate => IsUpdateReady && !IsUpdateBusy && !IsUpdateInstallHandoffActive;

        public bool IsUpdateInstallVisible => CanInstallUpdate;

        public string DeviceName
        {
            get => _deviceName;
            private set => SetProperty(ref _deviceName, value);
        }

        public string ActionRequiredMessage
        {
            get => _actionRequiredMessage;
            private set
            {
                if (SetProperty(ref _actionRequiredMessage, value))
                {
                    _statusPresentationRevision++;
                    if (IsMissingDesktopSyncChangesApiMessage(value))
                    {
                        SetDesktopSyncChangesApiUnavailable(true);
                    }

                    OnPropertyChanged(nameof(HasActionRequired));
                    OnPropertyChanged(nameof(HasStatusAttention));
                    OnPropertyChanged(nameof(HeaderStatusText));
                    OnPropertyChanged(nameof(IsStatusCardVisible));
                    OnPropertyChanged(nameof(HasOfflineStatus));
                    OnPropertyChanged(nameof(ActionRequiredOpacity));
                    OnPropertyChanged(nameof(CanRetryActionRequired));
                    OnPropertyChanged(nameof(CanApproveRemoteMassDelete));
                    OnPropertyChanged(nameof(RemoteMassDeleteApprovalText));
                    OnPropertyChanged(nameof(RemoteMassDeleteApprovalToolTip));
                    ApproveRemoteMassDeleteCommand.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(StatusCardTitle));
                    OnPropertyChanged(nameof(StatusCardDetailText));
                    OnPropertyChanged(nameof(HasStatusCardDetail));
                    OnPropertyChanged(nameof(HasDashboardNotifications));
                    RaiseAddSyncPairFlowCommandStates();
                    RefreshCurrentProgressText();
                }
            }
        }

        public string GlobalStatus
        {
            get => _globalStatus;
            private set
            {
                if (SetProperty(ref _globalStatus, value))
                {
                    _statusPresentationRevision++;
                    OnPropertyChanged(nameof(HeaderStatusText));
                    OnPropertyChanged(nameof(StatusCardTitle));
                }
            }
        }

        public string HeaderStatusText
        {
            get
            {
                if (HasConflicts)
                {
                    return "Conflicts need review";
                }

                if (HasActionRequired || HasPairStatusAttention)
                {
                    if (HasOfflineSyncPairs && !HasActionRequired)
                    {
                        return "Offline";
                    }

                    return "Action required";
                }

                if (IsSyncPaused || IsSyncPausePending)
                {
                    return GlobalStatus;
                }

                return HasCurrentWorkProgress ? "Syncing" : GlobalStatus;
            }
        }

        public string HeaderTitleText => IsSignedIn ? ResolveAccountDisplayName(AccountName, null) : "Cotton Sync";

        public string StatusCardTitle
        {
            get
            {
                if (HasOfflineStatus)
                {
                    return "Offline";
                }

                if (HasActionRequired || HasPairStatusAttention)
                {
                    return "Sync needs attention";
                }

                return CurrentProgressText;
            }
        }

        public string StatusCardDetailText => HasActionRequired || HasPairStatusAttention || HasOfflineStatus
            ? CurrentProgressText
            : string.Empty;

        public bool HasStatusCardDetail => !string.IsNullOrWhiteSpace(StatusCardDetailText);
    }
}
