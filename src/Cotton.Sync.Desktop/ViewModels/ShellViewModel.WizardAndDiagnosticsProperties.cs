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
        public string TrayLifecycleStatusText => IsTrayLifecycleSupported
            ? "Closing the window keeps Cotton Sync running from the tray."
            : TrayLifecycleDetails;

        public bool IsAddSyncPairWizardVisible
        {
            get => _isAddSyncPairWizardVisible;
            private set
            {
                if (SetProperty(ref _isAddSyncPairWizardVisible, value))
                {
                    RaiseWizardStateProperties();
                    OnPropertyChanged(nameof(IsDashboardChromeVisible));
                    OnPropertyChanged(nameof(IsDashboardHeaderVisible));
                }
            }
        }

        public bool HasLocalFolderSelection => !string.IsNullOrWhiteSpace(LocalFolderPath);

        public bool IsAddSyncPairLocalStepVisible => IsAddSyncPairWizardVisible && !HasLocalFolderSelection;

        public bool IsAddSyncPairCloudStepVisible => IsAddSyncPairWizardVisible && HasLocalFolderSelection;

        public bool IsAddSyncPairLocalSummaryVisible => IsAddSyncPairCloudStepVisible;

        public bool IsCreateRemoteFolderVisible
        {
            get => _isCreateRemoteFolderVisible;
            private set
            {
                if (SetProperty(ref _isCreateRemoteFolderVisible, value))
                {
                    ShowCreateRemoteFolderCommand.RaiseCanExecuteChanged();
                    CreateRemoteFolderCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsSettingsVisible
        {
            get => _isSettingsVisible;
            private set
            {
                if (SetProperty(ref _isSettingsVisible, value))
                {
                    OnPropertyChanged(nameof(IsDashboardChromeVisible));
                    OnPropertyChanged(nameof(IsDashboardHeaderVisible));
                    CloseSettingsCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public int SelectedSettingsTabIndex
        {
            get => _selectedSettingsTabIndex;
            set => SetProperty(ref _selectedSettingsTabIndex, value);
        }

        public string AddSyncPairWizardTitle => HasLocalFolderSelection ? "Choose cloud folder" : "Choose local folder";

        public string AddSyncPairWizardSubtitle => HasLocalFolderSelection
            ? "Pick where this computer folder should sync in Cotton Cloud."
            : "Start with the folder on this computer.";

        public string RemoteFolderWizardPrimaryActionText => IsRemoteFolderLoading
            ? RemoteFolderLoadingMessage
            : "Use this folder";

        public string RemoteFolderWizardPrimaryActionToolTip => IsRemoteFolderLoading
            ? "Loading cloud folders"
            : "Start syncing with the current cloud folder";

        public bool IsFutureSyncModesVisible => _featureFlags.ShowFutureSyncModes && IsWindowsVirtualFilesSupported;

        public bool IsWindowsVirtualFilesSupported
        {
            get => _isWindowsVirtualFilesSupported;
            private set
            {
                if (SetProperty(ref _isWindowsVirtualFilesSupported, value))
                {
                    if (!value && SelectedSyncMode == SyncPairMode.WindowsVirtualFiles)
                    {
                        SelectedSyncMode = SyncPairMode.FullMirror;
                    }

                    OnPropertyChanged(nameof(IsFutureSyncModesVisible));
                }
            }
        }

        public string WindowsVirtualFilesDetails
        {
            get => _windowsVirtualFilesDetails;
            private set => SetProperty(ref _windowsVirtualFilesDetails, value);
        }

        public SyncPairMode SelectedSyncMode
        {
            get => _selectedSyncMode;
            set
            {
                SyncPairMode next = value == SyncPairMode.WindowsVirtualFiles && !IsWindowsVirtualFilesSupported
                    ? SyncPairMode.FullMirror
                    : value;
                if (SetProperty(ref _selectedSyncMode, next))
                {
                    OnPropertyChanged(nameof(IsFullMirrorSyncModeSelected));
                    OnPropertyChanged(nameof(IsWindowsVirtualFilesSyncModeSelected));
                    OnPropertyChanged(nameof(SelectedSyncModeLabel));
                    OnPropertyChanged(nameof(AddSyncPairSetupProgressMessage));
                }
            }
        }

        public bool IsFullMirrorSyncModeSelected
        {
            get => SelectedSyncMode == SyncPairMode.FullMirror;
            set
            {
                if (value)
                {
                    SelectedSyncMode = SyncPairMode.FullMirror;
                }
            }
        }

        public bool IsWindowsVirtualFilesSyncModeSelected
        {
            get => SelectedSyncMode == SyncPairMode.WindowsVirtualFiles;
            set
            {
                if (value)
                {
                    SelectedSyncMode = SyncPairMode.WindowsVirtualFiles;
                }
            }
        }

        public string SelectedSyncModeLabel => SelectedSyncMode == SyncPairMode.WindowsVirtualFiles
            ? VirtualFileUserFacingCopy.WindowsVirtualFilesModeLabel
            : "Full mirror";

        public string RemoteFolderSelectionLabel => string.IsNullOrWhiteSpace(RemoteFolderPath)
            ? "Cloud folder: /"
            : $"Cloud folder: {RemoteFolderPath}";

        public string RemoteFolderFilter
        {
            get => _remoteFolderFilter;
            set
            {
                if (SetProperty(ref _remoteFolderFilter, value))
                {
                    ApplyRemoteFolderFilter();
                    RaiseRemoteFolderListStateProperties();
                }
            }
        }

        public string RemoteFolderCountLabel
        {
            get
            {
                int total = _remoteFolderRows.Count;
                int visible = RemoteFolders.Count;
                if (total == 0)
                {
                    return "0 folders";
                }

                string totalLabel = total == 1 ? "1 folder" : total.ToString(CultureInfo.CurrentCulture) + " folders";
                if (visible == total)
                {
                    return totalLabel;
                }

                string visibleLabel = visible == 1 ? "1" : visible.ToString(CultureInfo.CurrentCulture);
                return visibleLabel + " of " + totalLabel;
            }
        }

        public bool HasRemoteFolderCount => _remoteFolderRows.Count > 0;

        public string RemoteFolderEmptyTitle => string.IsNullOrWhiteSpace(RemoteFolderFilter)
            ? "No folders here"
            : "No matching folders";

        public string RemoteFolderEmptySubtitle => string.IsNullOrWhiteSpace(RemoteFolderFilter)
            ? "The current cloud folder can still be selected."
            : "Try a different search or select the current cloud folder.";

        public bool IsServerProbeChecking
        {
            get => _isServerProbeChecking;
            private set => SetProperty(ref _isServerProbeChecking, value);
        }

        public bool IsServerProbeFailed
        {
            get => _isServerProbeFailed;
            private set => SetProperty(ref _isServerProbeFailed, value);
        }

        public bool IsServerVerified
        {
            get => _isServerVerified;
            private set
            {
                if (SetProperty(ref _isServerVerified, value))
                {
                    SignInCommand.RaiseCanExecuteChanged();
                    SignInWithBrowserCommand.RaiseCanExecuteChanged();
                    RaiseSetupStateProperties();
                }
            }
        }

        public string LocalFolderPath
        {
            get => _localFolderPath;
            set
            {
                if (SetProperty(ref _localFolderPath, value))
                {
                    AddSyncPairCommand.RaiseCanExecuteChanged();
                    UseRemoteFolderCommand.RaiseCanExecuteChanged();
                    RaiseWizardStateProperties();
                }
            }
        }

        public string LastDiagnosticsBundlePath
        {
            get => _lastDiagnosticsBundlePath;
            private set
            {
                if (SetProperty(ref _lastDiagnosticsBundlePath, value))
                {
                    OnPropertyChanged(nameof(HasLastDiagnosticsBundlePath));
                    OpenDiagnosticsBundleFolderCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasLastDiagnosticsBundlePath => !string.IsNullOrWhiteSpace(LastDiagnosticsBundlePath);

        public string DataDirectory
        {
            get => _dataDirectory;
            private set
            {
                if (SetProperty(ref _dataDirectory, value))
                {
                    OnPropertyChanged(nameof(HasDataDirectory));
                    OpenDataFolderCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasDataDirectory => !string.IsNullOrWhiteSpace(DataDirectory);

        public string AppDatabasePath
        {
            get => _appDatabasePath;
            private set => SetProperty(ref _appDatabasePath, value);
        }

        public string SyncStateDatabasePath
        {
            get => _syncStateDatabasePath;
            private set => SetProperty(ref _syncStateDatabasePath, value);
        }

        public string TokenStorePath
        {
            get => _tokenStorePath;
            private set => SetProperty(ref _tokenStorePath, value);
        }

        public string NewRemoteFolderName
        {
            get => _newRemoteFolderName;
            set
            {
                if (SetProperty(ref _newRemoteFolderName, value))
                {
                    CreateRemoteFolderCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                if (SetProperty(ref _password, value))
                {
                    SignInCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string RemoteFolderPath
        {
            get => _remoteFolderPath;
            set
            {
                if (SetProperty(ref _remoteFolderPath, value))
                {
                    AddSyncPairCommand.RaiseCanExecuteChanged();
                    UseRemoteFolderCommand.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(RemoteFolderSelectionLabel));
                }
            }
        }

        public string RemoteBrowserPath
        {
            get => _remoteBrowserPath;
            private set
            {
                if (SetProperty(ref _remoteBrowserPath, value))
                {
                    RemoteFolderUpCommand.RaiseCanExecuteChanged();
                }
            }
        }
    }
}
