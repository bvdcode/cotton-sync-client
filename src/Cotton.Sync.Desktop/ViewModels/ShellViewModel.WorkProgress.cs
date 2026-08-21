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
        private void SetSnapshotLoading(bool isLoading)
        {
            if (SetProperty(ref _isLoadingSnapshot, isLoading, nameof(IsStartupLoadingVisible)))
            {
                OnPropertyChanged(nameof(IsDashboardVisible));
                OnPropertyChanged(nameof(IsDashboardHeaderVisible));
                OnPropertyChanged(nameof(IsSetupVisible));
                RaiseSetupStateProperties();
                RaiseCommandStates();
            }
        }

        private void RaiseWizardStateProperties()
        {
            OnPropertyChanged(nameof(HasLocalFolderSelection));
            OnPropertyChanged(nameof(IsAddSyncPairLocalStepVisible));
            OnPropertyChanged(nameof(IsAddSyncPairCloudStepVisible));
            OnPropertyChanged(nameof(IsCreateRemoteFolderVisible));
            OnPropertyChanged(nameof(IsRemoteFolderLoadingVisible));
            OnPropertyChanged(nameof(IsAddSyncPairSetupProgressVisible));
            OnPropertyChanged(nameof(AddSyncPairWizardTitle));
            OnPropertyChanged(nameof(AddSyncPairWizardSubtitle));
            OnPropertyChanged(nameof(IsAddSyncPairLocalSummaryVisible));
            OnPropertyChanged(nameof(RemoteFolderWizardPrimaryActionText));
            OnPropertyChanged(nameof(RemoteFolderWizardPrimaryActionToolTip));
            ShowCreateRemoteFolderCommand.RaiseCanExecuteChanged();
            CreateRemoteFolderCommand.RaiseCanExecuteChanged();
            UseRemoteFolderCommand.RaiseCanExecuteChanged();
        }

        private void SetAllPairStatuses(string status, string? currentOperation = null, bool enabledOnly = false)
        {
            foreach (SyncPairRowViewModel syncPair in SyncPairs)
            {
                if (enabledOnly && !syncPair.IsEnabled)
                {
                    continue;
                }

                syncPair.Status = status;
                syncPair.CurrentOperation = currentOperation ?? string.Empty;
            }

            RaiseSyncStateProperties();
            SyncNowCommand.RaiseCanExecuteChanged();
            ApproveRemoteMassDeleteCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
        }

        private void RefreshCurrentProgressText()
        {
            CurrentProgressText = ResolveCurrentProgressText();
        }

        private string ResolveCurrentProgressText()
        {
            if (!IsSignedIn)
            {
                return ResolveSignedOutProgressText();
            }

            if (IsExportingDiagnostics)
            {
                return DiagnosticsExportProgressMessage;
            }

            if (SyncPairs.Count == 0)
            {
                return string.Empty;
            }

            string? attentionText = ResolveSyncAttentionProgressText();
            return attentionText ?? ResolveSyncActivityProgressText();
        }

        private string ResolveSignedOutProgressText()
        {
            if (HasStoredSession)
            {
                return "Waiting for Cotton Cloud to reconnect.";
            }

            return HasActionRequired ? "Sign in to continue." : "Sign in to start sync.";
        }

        private string? ResolveSyncAttentionProgressText()
        {
            if (HasActionRequired)
            {
                return "Fix the issue below to continue syncing.";
            }

            if (IsRemovingSyncPair)
            {
                return RemoveSyncPairProgressMessage;
            }

            if (HasOfflineSyncPairs)
            {
                return "Waiting for connection to recover.";
            }

            SyncPairRowViewModel? waitingPair = SyncPairs.FirstOrDefault(static pair =>
                string.Equals(pair.Status, "Waiting", StringComparison.Ordinal));
            if (waitingPair is not null)
            {
                return CreateWaitingPairProgressText(waitingPair);
            }

            if (HasConflicts)
            {
                return "Review conflicts below to continue syncing.";
            }

            if (HasPairStatusAttention)
            {
                return "Fix the folder issue to continue syncing.";
            }

            return !HasEnabledSyncPairs ? "Enable a folder to start syncing." : null;
        }

        private static string CreateWaitingPairProgressText(SyncPairRowViewModel waitingPair)
        {
            return string.IsNullOrWhiteSpace(waitingPair.CurrentOperation)
                ? waitingPair.DisplayName + ": Waiting for a local file."
                : waitingPair.DisplayName + ": " + waitingPair.CurrentOperation;
        }

        private string ResolveSyncActivityProgressText()
        {
            SyncPairRowViewModel? activePair = SyncPairs.FirstOrDefault(IsActiveProgressPair);
            if (activePair is not null)
            {
                return CreateActivePairProgressText(activePair);
            }

            if (SyncPairs.Any(static pair => string.Equals(pair.Status, "Paused", StringComparison.Ordinal)))
            {
                return "Sync is paused.";
            }

            if (SyncPairs.Any(static pair => pair.IsEnabled && pair.LastSyncedAtUtc is null))
            {
                return "Waiting for first sync.";
            }

            return "All folders are up to date.";
        }

        private static string CreateActivePairProgressText(SyncPairRowViewModel activePair)
        {
            string operation = string.IsNullOrWhiteSpace(activePair.CurrentOperation)
                ? activePair.Status
                : activePair.CurrentOperation;
            return activePair.DisplayName + ": " + operation;
        }

        private void ClearTransferProgress()
        {
            lock (_progressDispatchGate)
            {
                _pendingCoalescedTransferProgress = null;
                _isCoalescedTransferProgressDispatchScheduled = false;
                _lastVisibleTransferProgressAtUtc = null;
                _visibleTransferSyncPairId = null;
                _visibleTransferDirection = SyncTransferDirection.Unknown;
                _visibleTransferRelativePath = string.Empty;
            }

            HasCurrentTransfer = false;
            IsCurrentTransferIndeterminate = false;
            CurrentTransferProgressValue = 0;
            CurrentTransferTitle = string.Empty;
            CurrentTransferDetails = string.Empty;
            _transferProgressByKey.Clear();
            _transferSyncPairId = null;
            _transferDirection = SyncTransferDirection.Unknown;
            _transferRelativePath = string.Empty;
            RaiseCurrentWorkProgressProperties();
        }

        private void ClearRunProgress()
        {
            _runProgressByPair.Clear();
            _runProgressAppliedAtUtcByPair.Clear();
            ClearRunTransferMetrics();
            lock (_progressDispatchGate)
            {
                _pendingCoalescedRunProgress = null;
                _isCoalescedRunProgressDispatchScheduled = false;
                _lastVisibleRunProgressAtUtc = null;
                _visibleRunProgressSyncPairId = null;
                _visibleRunProgressStage = SyncRunProgressStage.Unknown;
            }

            HasCurrentRunProgress = false;
            IsCurrentRunProgressIndeterminate = false;
            CurrentRunProgressValue = 0;
            CurrentRunProgressTitle = string.Empty;
            CurrentRunProgressDetails = string.Empty;
            RaiseCurrentWorkProgressProperties();
        }

        private void RefreshRunProgressSummary(bool updateEstimate = true)
        {
            List<DesktopRunProgressSnapshot> progressValues = GetOrderedRunProgressSnapshots();
            if (progressValues.Count == 0)
            {
                ClearRunProgress();
                return;
            }

            HasCurrentRunProgress = true;
            ExpireStaleRunTransferRate(progressValues);
            if (updateEstimate)
            {
                UpdateRunProgressEstimatedTimeRemaining(progressValues);
                UpdateRunTransferEstimatedTimeRemaining(progressValues);
            }

            if (progressValues.Count == 1)
            {
                DesktopRunProgressSnapshot progress = progressValues[0];
                SyncPairRowViewModel syncPair = SyncPairs.First(pair => pair.Id == progress.SyncPairId);
                IsCurrentRunProgressIndeterminate = IsIndeterminateRunProgress(progress);
                CurrentRunProgressValue = CalculateRunProgressValue(progress);
                CurrentRunProgressTitle = syncPair.DisplayName;
                CurrentRunProgressDetails = CreateSingleRunProgressDetails(progress);
                RaiseCurrentWorkProgressProperties();
                return;
            }

            IsCurrentRunProgressIndeterminate = progressValues.Any(IsIndeterminateRunProgress);
            CurrentRunProgressValue = CalculateAggregateRunProgressValue(progressValues);
            CurrentRunProgressTitle = "Syncing " + progressValues.Count.ToString(CultureInfo.CurrentCulture) + " folders";
            CurrentRunProgressDetails = CreateAggregateRunProgressDetails(progressValues);
            RaiseCurrentWorkProgressProperties();
        }

        private void ExpireStaleRunTransferRate(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            if (HasActiveTransferProgress
                || !_runTransferSpeedBytesPerSecond.HasValue
                || _runTransferSamples.Count == 0)
            {
                return;
            }

            DateTime latestRunProgressAtUtc = progressValues
                .Max(static progress => progress.OccurredAtUtc.ToUniversalTime());
            DateTime latestTransferProgressAtUtc = _runTransferSamples.Last().OccurredAtUtc.ToUniversalTime();
            if (latestRunProgressAtUtc - latestTransferProgressAtUtc <= RunTransferMetricsWindow)
            {
                return;
            }

            _runTransferSamples.Clear();
            _runTransferSpeedBytesPerSecond = null;
            _lastRunTransferSpeedOccurredAtUtc = null;
            _runTransferEstimatedTimeRemaining = null;
            _lastRunTransferEstimateOccurredAtUtc = null;
        }

        private List<DesktopRunProgressSnapshot> GetOrderedRunProgressSnapshots()
        {
            var progressValues = new List<DesktopRunProgressSnapshot>();
            foreach (SyncPairRowViewModel syncPair in SyncPairs)
            {
                if (_runProgressByPair.TryGetValue(syncPair.Id, out DesktopRunProgressSnapshot? progress))
                {
                    progressValues.Add(progress);
                }
            }

            return progressValues;
        }

        private void RaiseCurrentWorkProgressProperties()
        {
            OnPropertyChanged(nameof(HasCurrentWorkProgress));
            OnPropertyChanged(nameof(CurrentTrayActivityKind));
            OnPropertyChanged(nameof(HeaderStatusText));
            OnPropertyChanged(nameof(IsStatusCardVisible));
            OnPropertyChanged(nameof(HasDashboardNotifications));
            OnPropertyChanged(nameof(CurrentWorkProgressTitle));
            OnPropertyChanged(nameof(CurrentWorkProgressHeaderDetails));
            OnPropertyChanged(nameof(HasCurrentWorkProgressHeaderDetails));
            OnPropertyChanged(nameof(CurrentWorkProgressHeaderSizeDetails));
            OnPropertyChanged(nameof(HasCurrentWorkProgressHeaderSizeDetails));
            OnPropertyChanged(nameof(CurrentWorkProgressHeaderRateDetails));
            OnPropertyChanged(nameof(HasCurrentWorkProgressHeaderRateDetails));
            OnPropertyChanged(nameof(CurrentWorkProgressDetails));
            OnPropertyChanged(nameof(CurrentWorkProgressSecondaryDetails));
            OnPropertyChanged(nameof(HasCurrentWorkProgressSecondaryDetails));
            OnPropertyChanged(nameof(CurrentWorkProgressValue));
            OnPropertyChanged(nameof(IsCurrentWorkProgressIndeterminate));
            OnPropertyChanged(nameof(CurrentWorkProgressAutomationName));
        }

        private string CreateRunTransferSizeDetails()
        {
            if (TryCalculateAggregateRunTransferBytes(out long transferredBytes, out long totalBytes))
            {
                return FormatBytes(transferredBytes) + " / " + FormatBytes(totalBytes);
            }

            if (_runTransferredBytes > 0)
            {
                return FormatBytes(_runTransferredBytes);
            }

            return HasActiveTransferProgress
                ? CreateAggregateTransferMetricDetails(_transferProgressByKey.Values).Size
                : string.Empty;
        }

        private string CreateRunTransferRateDetails()
        {
            List<string> parts = [];
            (bool hasByteRate, bool hasByteEstimate) = AddRunByteRateDetails(parts);

            if (!hasByteRate && _currentRunProgressFilesPerSecond is > 0)
            {
                parts.Add(FormatCurrentRunProgressRate(_currentRunProgressFilesPerSecond.Value));
            }

            if (!hasByteEstimate && _currentRunProgressEstimatedTimeRemaining.HasValue)
            {
                parts.Add(FormatDuration(_currentRunProgressEstimatedTimeRemaining.Value) + " left");
            }

            return string.Join(" · ", parts);
        }
    }
}
