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
        private void ApplyRunProgress(DesktopRunProgressSnapshot progress)
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault(pair => pair.Id == progress.SyncPairId);
            if (syncPair is null || progress.Stage == SyncRunProgressStage.Unknown)
            {
                return;
            }

            if (progress.IsCompleted)
            {
                _runProgressByPair.Remove(progress.SyncPairId);
                _runProgressAppliedAtUtcByPair.Remove(progress.SyncPairId);
                _suppressedInitialSyncCompleteUntilRunProgressCompleted.Remove(progress.SyncPairId);
                if (!HasActiveTransferForPair(progress.SyncPairId))
                {
                    ClearSyncPairProgress(syncPair);
                }

                RefreshRunProgressSummary();
                RefreshCurrentProgressText();
                return;
            }

            _runProgressByPair[progress.SyncPairId] = progress;
            _runProgressAppliedAtUtcByPair[progress.SyncPairId] = DateTime.UtcNow;
            bool hasActiveTransferForPair = HasActiveTransferForPair(progress.SyncPairId);
            if (!hasActiveTransferForPair)
            {
                syncPair.CurrentOperation = CreateRunProgressOperation(progress);
            }

            syncPair.HasCurrentProgress = true;
            syncPair.IsCurrentProgressIndeterminate = IsIndeterminateRunProgress(progress);
            syncPair.CurrentProgressValue = CalculateRunProgressValue(progress);
            RefreshRunProgressSummary();
            RefreshCurrentProgressText();
        }

        private void ApplyStatus(DesktopSyncStatusSnapshot status)
        {
            HashSet<Guid> suppressedInitialSyncCompletePairIds = GetInitialSyncCompleteNotificationSuppressionIds();
            bool hasActiveSyncStatus = false;
            bool runProgressChanged = false;
            bool transferProgressChanged = false;
            foreach (DesktopSyncPairStatusSnapshot pairStatus in status.SyncPairs)
            {
                SyncPairRowViewModel? row = SyncPairs.FirstOrDefault(syncPair => syncPair.Id == pairStatus.Id);
                if (row is null)
                {
                    continue;
                }

                (bool IsActive, bool RunProgressChanged, bool TransferProgressChanged) application =
                    ApplySyncPairStatus(row, pairStatus, suppressedInitialSyncCompletePairIds);
                hasActiveSyncStatus |= application.IsActive;
                runProgressChanged |= application.RunProgressChanged;
                transferProgressChanged |= application.TransferProgressChanged;
            }

            GlobalStatus = ResolveGlobalStatus(status);
            ActionRequiredMessage = DesktopActionRequiredMessageResolver.FromStatus(status);
            OnPropertyChanged(nameof(IsStatusCardVisible));
            OnPropertyChanged(nameof(HasDashboardNotifications));
            RefreshDetailedProgress(hasActiveSyncStatus, runProgressChanged, transferProgressChanged);
            RaiseSyncStateProperties();
            RefreshCurrentProgressText();
            AddNotifications(_notificationTracker.Apply(
                status,
                SyncPairs.ToDictionary(static pair => pair.Id, static pair => pair.DisplayName),
                suppressedInitialSyncCompletePairIds));
            RefreshDiagnosticsItems();
        }

        private (bool IsActive, bool RunProgressChanged, bool TransferProgressChanged) ApplySyncPairStatus(
            SyncPairRowViewModel row,
            DesktopSyncPairStatusSnapshot pairStatus,
            IReadOnlySet<Guid> suppressedInitialSyncCompletePairIds)
        {
            bool isActiveStatus = IsActiveSyncStatus(pairStatus);
            bool hasFreshDetailedProgress = HasFreshDetailedProgress(pairStatus.Id);
            bool keepProgress = suppressedInitialSyncCompletePairIds.Contains(pairStatus.Id)
                && hasFreshDetailedProgress;
            ApplySyncPairStatusValues(row, pairStatus, isActiveStatus, keepProgress, hasFreshDetailedProgress);
            (bool RunProgressChanged, bool TransferProgressChanged) progressChanges =
                ApplySyncPairStatusProgress(row, pairStatus, isActiveStatus || keepProgress);
            ApplySyncPairStatusActivity(row, pairStatus);
            return (isActiveStatus || keepProgress, progressChanges.RunProgressChanged, progressChanges.TransferProgressChanged);
        }

        private static void ApplySyncPairStatusValues(
            SyncPairRowViewModel row,
            DesktopSyncPairStatusSnapshot pairStatus,
            bool isActiveStatus,
            bool keepProgress,
            bool hasFreshDetailedProgress)
        {
            row.Status = keepProgress ? "Syncing" : pairStatus.Status;
            row.IsEnabled = !string.Equals(pairStatus.Status, "Disabled", StringComparison.Ordinal);
            row.LastError = pairStatus.LastError;
            if ((!isActiveStatus && !keepProgress) || !hasFreshDetailedProgress)
            {
                row.CurrentOperation = pairStatus.CurrentOperation ?? string.Empty;
            }

            if (pairStatus.LastSyncedAtUtc.HasValue)
            {
                row.LastSyncedAtUtc = pairStatus.LastSyncedAtUtc;
            }
        }

        private (bool RunProgressChanged, bool TransferProgressChanged) ApplySyncPairStatusProgress(
            SyncPairRowViewModel row,
            DesktopSyncPairStatusSnapshot pairStatus,
            bool isActive)
        {
            if (isActive)
            {
                EnsureSyncPairProgress(row);
                return (false, false);
            }

            ClearSyncPairProgress(row);
            if (string.Equals(pairStatus.Status, "Waiting", StringComparison.Ordinal))
            {
                row.CurrentOperation = pairStatus.CurrentOperation ?? string.Empty;
            }

            bool runProgressChanged = _runProgressByPair.Remove(pairStatus.Id);
            _runProgressAppliedAtUtcByPair.Remove(pairStatus.Id);
            bool transferProgressChanged = RemoveTransferProgressForPair(pairStatus.Id);
            return (runProgressChanged, transferProgressChanged);
        }

        private void ApplySyncPairStatusActivity(
            SyncPairRowViewModel row,
            DesktopSyncPairStatusSnapshot pairStatus)
        {
            if (!ShouldAddStatusErrorActivity(pairStatus))
            {
                return;
            }

            string rawError = pairStatus.LastError ?? string.Empty;
            string activityMessage = DesktopActionRequiredMessageResolver.FromSyncPairStatus(pairStatus);
            AddActivity(
                "Error",
                row.LocalPath,
                string.IsNullOrWhiteSpace(activityMessage) ? rawError : activityMessage);
        }

        private void RefreshDetailedProgress(
            bool hasActiveSyncStatus,
            bool runProgressChanged,
            bool transferProgressChanged)
        {
            if (!hasActiveSyncStatus)
            {
                ClearTransferProgress();
                ClearRunProgress();
                return;
            }

            if (transferProgressChanged)
            {
                RefreshCurrentTransferSummary();
            }

            if (runProgressChanged)
            {
                RefreshRunProgressSummary();
            }
        }

        private bool ShouldAddStatusErrorActivity(DesktopSyncPairStatusSnapshot pairStatus)
        {
            if (string.Equals(pairStatus.Status, "Waiting", StringComparison.Ordinal))
            {
                _lastStatusErrorActivityMessages.Remove(pairStatus.Id);
                return false;
            }

            if (string.IsNullOrWhiteSpace(pairStatus.LastError))
            {
                _lastStatusErrorActivityMessages.Remove(pairStatus.Id);
                return false;
            }

            if (_lastStatusErrorActivityMessages.TryGetValue(pairStatus.Id, out string? lastError)
                && string.Equals(lastError, pairStatus.LastError, StringComparison.Ordinal))
            {
                return false;
            }

            _lastStatusErrorActivityMessages[pairStatus.Id] = pairStatus.LastError;
            return true;
        }

        private HashSet<Guid> GetInitialSyncCompleteNotificationSuppressionIds()
        {
            var syncPairIds = new HashSet<Guid>(_suppressedInitialSyncCompleteUntilRunProgressCompleted);
            foreach (DesktopRunProgressSnapshot progress in _runProgressByPair.Values)
            {
                if (!ShouldSuppressInitialSyncCompleteForRunProgress(progress.Stage)
                    || progress.IsCompleted
                    || !HasFreshDetailedProgress(progress.SyncPairId))
                {
                    continue;
                }

                SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault(pair => pair.Id == progress.SyncPairId);
                if (syncPair?.Mode == SyncPairMode.WindowsVirtualFiles)
                {
                    syncPairIds.Add(progress.SyncPairId);
                    _suppressedInitialSyncCompleteUntilRunProgressCompleted.Add(progress.SyncPairId);
                }
            }

            return syncPairIds;
        }

        private static bool ShouldSuppressInitialSyncCompleteForRunProgress(SyncRunProgressStage stage)
        {
            return stage is SyncRunProgressStage.CreatingPlaceholders
                or SyncRunProgressStage.FinalizingCloudFiles
                or SyncRunProgressStage.HydratingCloudFiles
                or SyncRunProgressStage.DehydratingCloudFiles;
        }

        private bool HasFreshDetailedProgress(Guid syncPairId)
        {
            if (HasActiveTransferForPair(syncPairId)
                || (_transferSyncPairId == syncPairId && HasCurrentTransfer))
            {
                return true;
            }

            if (!_runProgressByPair.ContainsKey(syncPairId))
            {
                return false;
            }

            if (!_runProgressAppliedAtUtcByPair.TryGetValue(syncPairId, out DateTime appliedAtUtc)
                || DateTime.UtcNow - appliedAtUtc.ToUniversalTime() <= ActiveStatusRunProgressStaleThreshold)
            {
                return true;
            }

            _runProgressByPair.Remove(syncPairId);
            _runProgressAppliedAtUtcByPair.Remove(syncPairId);
            RefreshRunProgressSummary();
            return false;
        }
    }
}
