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
        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                if (SetProperty(ref _serverUrl, value))
                {
                    if (!_isLoadingSnapshot && !IsSignedIn && HasActionRequired)
                    {
                        ActionRequiredMessage = string.Empty;
                    }

                    ScheduleServerProbe(value);
                    SignInCommand.RaiseCanExecuteChanged();
                    SignInWithBrowserCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string ServerProbeStatus
        {
            get => _serverProbeStatus;
            private set
            {
                if (SetProperty(ref _serverProbeStatus, value))
                {
                    OnPropertyChanged(nameof(HasServerProbeStatus));
                }
            }
        }

        public bool HasServerProbeStatus => !string.IsNullOrWhiteSpace(ServerProbeStatus);

        public RemoteFolderRowViewModel? SelectedRemoteFolder
        {
            get => _selectedRemoteFolder;
            set
            {
                if (SetProperty(ref _selectedRemoteFolder, value))
                {
                    OpenRemoteFolderCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ConflictRowViewModel? SelectedConflict
        {
            get => _selectedConflict;
            set => SetProperty(ref _selectedConflict, value);
        }

        public SyncPairRowViewModel? SelectedSyncPair
        {
            get => _selectedSyncPair;
            set
            {
                SyncPairRowViewModel? previous = _selectedSyncPair;
                if (SetProperty(ref _selectedSyncPair, value))
                {
                    if (previous is not null)
                    {
                        previous.IsEditorVisible = false;
                    }

                    UpdateSelectedSyncPairEditorVisibility();
                    OnPropertyChanged(nameof(SelectedSyncPairEditableDisplayName));
                    OnPropertyChanged(nameof(SelectedSyncPairToggleEnabledLabel));
                    OnPropertyChanged(nameof(AddSyncPairWizardSubtitle));
                    if (_pendingRemoveSyncPair is not null && !ReferenceEquals(_pendingRemoveSyncPair, value))
                    {
                        ClearRemoveSyncPairConfirmation();
                    }

                    OpenFolderCommand.RaiseCanExecuteChanged();
                    ToggleSelectedSyncPairEnabledCommand.RaiseCanExecuteChanged();
                    SaveSelectedSyncPairNameCommand.RaiseCanExecuteChanged();
                    RemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
                    ShowSelectedSyncPairEditorCommand.RaiseCanExecuteChanged();
                    ConfirmRemoveSelectedSyncPairCommand.RaiseCanExecuteChanged();
                    CancelRemoveSyncPairCommand.RaiseCanExecuteChanged();
                    CancelSelectedSyncPairEditorCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsRemoveSyncPairConfirmationVisible => _pendingRemoveSyncPair is not null;

        public bool IsRemoveSyncPairConfirmationActionsVisible => IsRemoveSyncPairConfirmationVisible && !IsRemovingSyncPair;

        public string RemoveSyncPairConfirmationTitle => _pendingRemoveSyncPair is null
            ? "Remove sync folder?"
            : IsRemovingSyncPair
                ? "Removing " + _pendingRemoveSyncPair.DisplayName
            : "Remove " + _pendingRemoveSyncPair.DisplayName + "?";

        public string RemoveSyncPairConfirmationMessage => _pendingRemoveSyncPair?.Mode == SyncPairMode.WindowsVirtualFiles
            ? IsRemovingSyncPair
                ? "Removing the Cloud Files registration and local placeholder folder. This can take a few minutes for large online-only folders."
                : "Stops syncing this folder. Cloud files stay online; the local placeholder folder is removed when it has no regular local files."
            : IsRemovingSyncPair
                ? "Removing this sync folder from the client."
            : "Stops syncing this folder. Local files stay on this device; cloud files stay online.";

        public bool IsRemovingSyncPair
        {
            get => _isRemovingSyncPair;
            private set
            {
                if (SetProperty(ref _isRemovingSyncPair, value))
                {
                    OnPropertyChanged(nameof(IsRemoveSyncPairConfirmationActionsVisible));
                    OnPropertyChanged(nameof(RemoveSyncPairConfirmationTitle));
                    OnPropertyChanged(nameof(RemoveSyncPairConfirmationMessage));
                    OnPropertyChanged(nameof(RemoveSyncPairProgressMessage));
                }
            }
        }

        public string RemoveSyncPairProgressMessage => _pendingRemoveSyncPair?.Mode == SyncPairMode.WindowsVirtualFiles
            ? "Removing Cloud Files sync root and cleaning local placeholder folder. Large online-only folders can take a few minutes."
            : "Removing sync folder.";

        public string SelectedSyncPairEditableDisplayName
        {
            get => SelectedSyncPair?.EditableDisplayName ?? string.Empty;
            set
            {
                if (SelectedSyncPair is { } selected)
                {
                    selected.EditableDisplayName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedSyncPairToggleEnabledLabel => SelectedSyncPair?.ToggleEnabledLabel ?? "Enable";

        public string TotpCode
        {
            get => _totpCode;
            set => SetProperty(ref _totpCode, value);
        }

        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value))
                {
                    SignInCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public void Dispose()
        {
            DisposeViewModelResources();
            _controller.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            DisposeViewModelResources();
            await _controller.DisposeAsync().ConfigureAwait(true);
        }

        private void DisposeViewModelResources()
        {
            _controller.StatusChanged -= OnStatusChanged;
            _controller.ActivityReported -= OnActivityReported;
            _controller.SessionRevoked -= OnSessionRevoked;
            _controller.TransferProgressChanged -= OnTransferProgressChanged;
            _controller.RunProgressChanged -= OnRunProgressChanged;
            Activities.CollectionChanged -= OnActivitiesChanged;
            Conflicts.CollectionChanged -= OnConflictsChanged;
            SyncPairs.CollectionChanged -= OnSyncPairsChanged;
            RemoteFolders.CollectionChanged -= OnRemoteFoldersChanged;
            SelfTestItems.CollectionChanged -= OnSelfTestItemsChanged;
            Notifications.CollectionChanged -= OnNotificationsChanged;
            _serverProbeCancellation?.Cancel();
            _serverProbeCancellation?.Dispose();
            _serverProbeCancellation = null;
            _browserSignInCancellation?.Cancel();
            _browserSignInCancellation?.Dispose();
            _browserSignInCancellation = null;
            _startupUpdateCancellation?.Cancel();
            _startupUpdateCancellation?.Dispose();
            _startupUpdateCancellation = null;
            _periodicUpdateCancellation?.Cancel();
            _periodicUpdateCancellation?.Dispose();
            _periodicUpdateCancellation = null;
            CancelStoredSessionRetry();
        }

        public async Task InitializeAsync()
        {
            IsBusy = true;
            SetSnapshotLoading(true);
            try
            {
                DesktopShellSnapshot snapshot = await _controller.LoadAsync().ConfigureAwait(true);
                ApplyInitialSnapshot(snapshot);
                RefreshDiagnosticsItems();
                RaiseCommandStates();
                BeginStartupUpdateCheck();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                HandleCommandError(exception);
            }
            finally
            {
                SetSnapshotLoading(false);
                IsBusy = false;
                BeginStoredSessionRetry();
            }
        }

        private void ApplyInitialSnapshot(DesktopShellSnapshot snapshot)
        {
            ApplyInitialConnectionSettings(snapshot);
            ApplyInitialPlatformSettings(snapshot);
            ApplyInitialDataPaths(snapshot);
            ReplaceSyncPairs(snapshot.SyncPairs);
            ApplyInitialSessionState(snapshot);
            RefreshCurrentProgressText();
            AddActivity("App", string.Empty, "Settings loaded");
            ApplyInitialSessionActivity(snapshot);
        }

        private void ApplyInitialConnectionSettings(DesktopShellSnapshot snapshot)
        {
            ServerUrl = snapshot.ServerUrl?.AbsoluteUri ?? string.Empty;
            Username = snapshot.RememberedUsername ?? string.Empty;
            StartWithOperatingSystem = snapshot.StartWithOperatingSystem;
            EnableNotifications = snapshot.EnableNotifications;
            ThemeModeIndex = (int)snapshot.ThemeMode;
            DeviceName = string.IsNullOrWhiteSpace(snapshot.DeviceName)
                ? "Cotton Sync Desktop"
                : snapshot.DeviceName.Trim();
        }

        private void ApplyInitialPlatformSettings(DesktopShellSnapshot snapshot)
        {
            IsStartWithOperatingSystemSupported = snapshot.PlatformCapabilities.IsAutostartSupported;
            IsTrayLifecycleSupported = snapshot.PlatformCapabilities.IsTrayLifecycleSupported;
            TrayLifecycleDetails = snapshot.PlatformCapabilities.TrayLifecycleDetails;
            IsWindowsVirtualFilesSupported = snapshot.PlatformCapabilities.IsWindowsVirtualFilesSupported;
            WindowsVirtualFilesDetails = snapshot.PlatformCapabilities.WindowsVirtualFilesDetails;
        }

        private void ApplyInitialDataPaths(DesktopShellSnapshot snapshot)
        {
            DataDirectory = snapshot.DataPaths.DataDirectory;
            AppDatabasePath = snapshot.DataPaths.AppDatabasePath;
            SyncStateDatabasePath = snapshot.DataPaths.SyncStateDatabasePath;
            TokenStorePath = snapshot.DataPaths.TokenStorePath;
        }

        private void ReplaceSyncPairs(IReadOnlyList<DesktopSyncPairSnapshot> syncPairs)
        {
            SyncPairs.Clear();
            foreach (DesktopSyncPairSnapshot syncPair in syncPairs)
            {
                SyncPairs.Add(ToRow(syncPair));
            }

            SelectedSyncPair = SyncPairs.FirstOrDefault();
        }

        private void ApplyInitialSessionState(DesktopShellSnapshot snapshot)
        {
            HasStoredSession = snapshot.HasStoredSession;
            IsSignedIn = snapshot.IsSignedIn;
            AccountName = snapshot.IsSignedIn
                ? ResolveAccountDisplayName(snapshot.AccountName, snapshot.RememberedUsername)
                : "Signed out";
            GlobalStatus = ResolveInitialGlobalStatus(snapshot);
        }

        private string ResolveInitialGlobalStatus(DesktopShellSnapshot snapshot)
        {
            if (snapshot.IsSignedIn)
            {
                return "Connected";
            }

            if (snapshot.HasStoredSession)
            {
                return "Waiting to reconnect";
            }

            return SyncPairs.Count == 0 ? "Ready to connect" : "Ready";
        }

        private void ApplyInitialSessionActivity(DesktopShellSnapshot snapshot)
        {
            if (snapshot.IsSignedIn)
            {
                AddActivity("Account", AccountName, "Session restored");
                if (_notifyOnSessionRestore)
                {
                    ShowNativeNotification("Session restored", AccountName);
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(snapshot.StartupErrorMessage))
            {
                return;
            }

            ApplyInitialSessionError(snapshot);
        }

        private void ApplyInitialSessionError(DesktopShellSnapshot snapshot)
        {
            string startupErrorMessage = snapshot.StartupErrorMessage ?? string.Empty;
            if (snapshot.HasStoredSession)
            {
                StoredSessionRestoreMessage = startupErrorMessage;
                ActionRequiredMessage = string.Empty;
                AddActivity("Warning", string.Empty, startupErrorMessage);
                return;
            }

            ActionRequiredMessage = startupErrorMessage;
            AddActivity("Error", string.Empty, startupErrorMessage);
        }
    }
}
