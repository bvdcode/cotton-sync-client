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
        public string CurrentProgressText
        {
            get => _currentProgressText;
            private set
            {
                if (SetProperty(ref _currentProgressText, value))
                {
                    OnPropertyChanged(nameof(StatusCardTitle));
                    OnPropertyChanged(nameof(StatusCardDetailText));
                    OnPropertyChanged(nameof(HasStatusCardDetail));
                }
            }
        }

        public bool HasCurrentTransfer
        {
            get => _hasCurrentTransfer;
            private set
            {
                if (SetProperty(ref _hasCurrentTransfer, value))
                {
                    OnPropertyChanged(nameof(IsCurrentTransferDeterminate));
                }
            }
        }

        public string CurrentTransferTitle
        {
            get => _currentTransferTitle;
            private set => SetProperty(ref _currentTransferTitle, value);
        }

        public string CurrentTransferDetails
        {
            get => _currentTransferDetails;
            private set => SetProperty(ref _currentTransferDetails, value);
        }

        public double CurrentTransferProgressValue
        {
            get => _currentTransferProgressValue;
            private set => SetProperty(ref _currentTransferProgressValue, value);
        }

        public bool IsCurrentTransferIndeterminate
        {
            get => _isCurrentTransferIndeterminate;
            private set
            {
                if (SetProperty(ref _isCurrentTransferIndeterminate, value))
                {
                    OnPropertyChanged(nameof(IsCurrentTransferDeterminate));
                }
            }
        }

        public bool IsCurrentTransferDeterminate => HasCurrentTransfer && !IsCurrentTransferIndeterminate;

        public bool HasCurrentRunProgress
        {
            get => _hasCurrentRunProgress;
            private set
            {
                if (SetProperty(ref _hasCurrentRunProgress, value))
                {
                    OnPropertyChanged(nameof(IsCurrentRunProgressDeterminate));
                }
            }
        }

        public string CurrentRunProgressTitle
        {
            get => _currentRunProgressTitle;
            private set => SetProperty(ref _currentRunProgressTitle, value);
        }

        public string CurrentRunProgressDetails
        {
            get => _currentRunProgressDetails;
            private set => SetProperty(ref _currentRunProgressDetails, value);
        }

        public double CurrentRunProgressValue
        {
            get => _currentRunProgressValue;
            private set => SetProperty(ref _currentRunProgressValue, value);
        }

        public bool IsCurrentRunProgressIndeterminate
        {
            get => _isCurrentRunProgressIndeterminate;
            private set
            {
                if (SetProperty(ref _isCurrentRunProgressIndeterminate, value))
                {
                    OnPropertyChanged(nameof(IsCurrentRunProgressDeterminate));
                }
            }
        }

        public bool IsCurrentRunProgressDeterminate => HasCurrentRunProgress && !IsCurrentRunProgressIndeterminate;

        public bool HasCurrentWorkProgress => HasCurrentTransfer || HasCurrentRunProgress;

        public DesktopTrayActivityKind CurrentTrayActivityKind
        {
            get
            {
                if (!HasCurrentWorkProgress)
                {
                    return DesktopTrayActivityKind.None;
                }

                if (_runProgressByPair.Values.Any(static progress =>
                        progress.Stage == SyncRunProgressStage.HydratingCloudFiles))
                {
                    return DesktopTrayActivityKind.MakingAvailable;
                }

                if (_runProgressByPair.Values.Any(static progress =>
                        progress.Stage == SyncRunProgressStage.DehydratingCloudFiles))
                {
                    return DesktopTrayActivityKind.FreeingSpace;
                }

                if (HasCurrentTransfer)
                {
                    return _transferDirection switch
                    {
                        SyncTransferDirection.Upload => DesktopTrayActivityKind.Uploading,
                        SyncTransferDirection.Download => DesktopTrayActivityKind.Downloading,
                        SyncTransferDirection.Hash => DesktopTrayActivityKind.Syncing,
                        SyncTransferDirection.Unknown => DesktopTrayActivityKind.Syncing,
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(_transferDirection),
                            _transferDirection,
                            "Unknown transfer direction cannot be shown in the tray."),
                    };
                }

                return DesktopTrayActivityKind.Syncing;
            }
        }

        public string CurrentWorkProgressTitle
        {
            get => IsRunProgressPrimary
                ? CurrentRunProgressTitle
                : HasCurrentTransfer ? CreateActiveTransferTitle() : CurrentRunProgressTitle;
        }

        public string CurrentWorkProgressHeaderDetails => IsRunProgressPrimary
            ? CreateHeaderDetails(CurrentWorkProgressHeaderSizeDetails, CurrentWorkProgressHeaderRateDetails)
            : string.Empty;

        public bool HasCurrentWorkProgressHeaderDetails => !string.IsNullOrWhiteSpace(CurrentWorkProgressHeaderDetails);

        public string CurrentWorkProgressHeaderSizeDetails => IsRunProgressPrimary
            ? CreateRunTransferSizeDetails()
            : string.Empty;

        public bool HasCurrentWorkProgressHeaderSizeDetails =>
            !string.IsNullOrWhiteSpace(CurrentWorkProgressHeaderSizeDetails)
            || !string.IsNullOrWhiteSpace(CurrentWorkProgressHeaderRateDetails);

        public string CurrentWorkProgressHeaderRateDetails => IsRunProgressPrimary
            ? CreateRunTransferRateDetails()
            : string.Empty;

        public bool HasCurrentWorkProgressHeaderRateDetails =>
            !string.IsNullOrWhiteSpace(CurrentWorkProgressHeaderRateDetails);

        public string CurrentWorkProgressDetails => IsRunProgressPrimary
            ? CurrentRunProgressDetails
            : HasCurrentTransfer ? CreateActiveTransferDetails() : CurrentRunProgressDetails;

        public string CurrentWorkProgressSecondaryDetails
        {
            get
            {
                if (IsRunProgressPrimary)
                {
                    return ShouldShowQueuedWorkIndicator()
                        ? QueuedWorkIndicatorText
                        : string.Empty;
                }

                return HasCurrentTransfer && HasCurrentRunProgress
                    ? CurrentRunProgressDetails
                    : string.Empty;
            }
        }

        public bool HasCurrentWorkProgressSecondaryDetails => !string.IsNullOrWhiteSpace(CurrentWorkProgressSecondaryDetails);

        public double CurrentWorkProgressValue => IsRunProgressPrimary
            ? CurrentRunProgressValue
            : TryCalculateAggregateTransferProgressValue(out double transferProgressValue)
                ? transferProgressValue
                : HasCurrentTransfer ? CurrentTransferProgressValue : CurrentRunProgressValue;

        public bool IsCurrentWorkProgressIndeterminate => IsRunProgressPrimary
            ? IsCurrentRunProgressIndeterminate
            : HasActiveTransferProgress
                ? !TryCalculateAggregateTransferProgressValue(out _)
                : HasCurrentTransfer ? IsCurrentTransferIndeterminate : IsCurrentRunProgressIndeterminate;

        public string CurrentWorkProgressAutomationName =>
            HasOpenEndedCloudFileProgress
                ? "Open-ended cloud file progress"
                : "Sync progress";

        private bool HasOpenEndedCloudFileProgress =>
            HasCurrentRunProgress
            && _runProgressByPair.Count > 0
            && _runProgressByPair.Values.All(static progress =>
                !progress.IsCompleted
                && progress.Stage == SyncRunProgressStage.CreatingPlaceholders);

        private bool IsRunProgressPrimary => HasCurrentRunProgress;

        private bool HasActiveTransferProgress => _transferProgressByKey.Count > 0;

        private bool ShouldShowQueuedWorkIndicator()
        {
            if (!HasCurrentRunProgress)
            {
                return false;
            }

            foreach (DesktopRunProgressSnapshot progress in _runProgressByPair.Values)
            {
                if (!IsQueuedWorkIndicatorStage(progress.Stage))
                {
                    continue;
                }

                int workCount = progress.FilesTotal ?? Math.Max(progress.FilesCompleted, 0);
                if (workCount >= QueuedWorkIndicatorFileThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsQueuedWorkIndicatorStage(SyncRunProgressStage stage)
        {
            return stage is SyncRunProgressStage.ReconcilingDirectories
                or SyncRunProgressStage.ReconcilingFiles;
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool IsExportingDiagnostics
        {
            get => _isExportingDiagnostics;
            private set
            {
                if (SetProperty(ref _isExportingDiagnostics, value))
                {
                    ExportDiagnosticsCommand.RaiseCanExecuteChanged();
                    OpenDiagnosticsBundleFolderCommand.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(IsStatusCardVisible));
                    OnPropertyChanged(nameof(HasDashboardNotifications));
                    OnPropertyChanged(nameof(HeaderStatusText));
                    OnPropertyChanged(nameof(StatusCardTitle));
                    OnPropertyChanged(nameof(StatusCardDetailText));
                    OnPropertyChanged(nameof(HasStatusCardDetail));
                    RefreshCurrentProgressText();
                }
            }
        }

        public string DiagnosticsExportProgressMessage => "Collecting logs and diagnostic state.";

        public bool IsRemoteFolderLoading
        {
            get => _isRemoteFolderLoading;
            private set
            {
                if (SetProperty(ref _isRemoteFolderLoading, value))
                {
                    OnPropertyChanged(nameof(IsRemoteFolderLoadingVisible));
                    OnPropertyChanged(nameof(RemoteFolderWizardPrimaryActionText));
                    OnPropertyChanged(nameof(RemoteFolderWizardPrimaryActionToolTip));
                }
            }
        }

        public bool IsRemoteFolderLoadingVisible => IsRemoteFolderLoading && IsAddSyncPairCloudStepVisible;

        public string RemoteFolderLoadingMessage => "Loading cloud folders";

        public bool IsAddingSyncPair
        {
            get => _isAddingSyncPair;
            private set
            {
                if (SetProperty(ref _isAddingSyncPair, value))
                {
                    OnPropertyChanged(nameof(AddSyncPairSetupProgressMessage));
                    OnPropertyChanged(nameof(RemoteFolderWizardPrimaryActionText));
                    OnPropertyChanged(nameof(RemoteFolderWizardPrimaryActionToolTip));
                    RaiseAddSyncPairFlowCommandStates();
                }
            }
        }

        public string AddSyncPairSetupProgressMessage => SelectedSyncMode == SyncPairMode.WindowsVirtualFiles
            ? "Connecting virtual files"
            : "Saving sync folder";

        public bool IsBrowserSignInPending
        {
            get => _isBrowserSignInPending;
            private set
            {
                if (SetProperty(ref _isBrowserSignInPending, value))
                {
                    OnPropertyChanged(nameof(BrowserSignInButtonText));
                    OnPropertyChanged(nameof(IsPasswordSignInVisible));
                    CancelBrowserSignInCommand.RaiseCanExecuteChanged();
                }
            }
        }
    }
}
