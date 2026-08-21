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
        private void ApplyTransferProgress(DesktopTransferProgressSnapshot progress)
        {
            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault(pair => pair.Id == progress.SyncPairId);
            if (syncPair is null || progress.Direction == SyncTransferDirection.Unknown)
            {
                return;
            }

            RunTransferProgressKey key = CreateTransferProgressKey(progress);
            if (IsSupersededTransferProgress(key, progress))
            {
                return;
            }

            TrackRunTransferProgress(progress);
            if (progress.IsCompleted)
            {
                CompleteTransferProgress(key, progress, syncPair);
                return;
            }

            ApplyActiveTransferProgress(key, progress, syncPair);
        }

        private bool IsSupersededTransferProgress(
            RunTransferProgressKey key,
            DesktopTransferProgressSnapshot progress)
        {
            return _transferProgressByKey.TryGetValue(key, out DesktopTransferProgressSnapshot? currentProgress)
                && progress.OccurredAtUtc < currentProgress.OccurredAtUtc;
        }

        private void CompleteTransferProgress(
            RunTransferProgressKey key,
            DesktopTransferProgressSnapshot progress,
            SyncPairRowViewModel syncPair)
        {
            if (_transferProgressByKey.TryGetValue(key, out DesktopTransferProgressSnapshot? activeProgress)
                && CanReplacePendingTransferProgress(activeProgress, progress))
            {
                _transferProgressByKey.Remove(key);
            }

            RefreshSyncPairProgressAfterTransfer(syncPair);
            RefreshCurrentTransferSummary();
            if (_runProgressByPair.Count > 0)
            {
                RefreshRunProgressSummary();
            }

            RefreshCurrentProgressText();
        }

        private void ApplyActiveTransferProgress(
            RunTransferProgressKey key,
            DesktopTransferProgressSnapshot progress,
            SyncPairRowViewModel syncPair)
        {
            _transferProgressByKey[key] = progress;
            SetCurrentTransferSummary(progress, syncPair);
            syncPair.CurrentOperation = CreateSyncPairTransferOperation(syncPair.Id, progress);
            syncPair.HasCurrentProgress = true;
            if (_runProgressByPair.TryGetValue(progress.SyncPairId, out DesktopRunProgressSnapshot? runProgress))
            {
                syncPair.IsCurrentProgressIndeterminate = IsIndeterminateRunProgress(runProgress);
                syncPair.CurrentProgressValue = CalculateRunProgressValue(runProgress);
                RefreshRunProgressSummary();
            }
            else
            {
                bool hasAggregateProgress = TryCalculateAggregateTransferProgressValue(
                    syncPair.Id,
                    out double aggregateProgressValue);
                syncPair.IsCurrentProgressIndeterminate = !hasAggregateProgress;
                syncPair.CurrentProgressValue = hasAggregateProgress ? aggregateProgressValue : 0;
                RaiseCurrentWorkProgressProperties();
            }

            RefreshCurrentProgressText();
        }

        private void RefreshSyncPairProgressAfterTransfer(SyncPairRowViewModel syncPair)
        {
            DesktopTransferProgressSnapshot? activeTransfer = GetLatestActiveTransferForPair(syncPair.Id);
            if (activeTransfer is not null)
            {
                syncPair.CurrentOperation = CreateSyncPairTransferOperation(syncPair.Id, activeTransfer);
                syncPair.HasCurrentProgress = true;
                if (_runProgressByPair.TryGetValue(syncPair.Id, out DesktopRunProgressSnapshot? activeRunProgress))
                {
                    syncPair.IsCurrentProgressIndeterminate = IsIndeterminateRunProgress(activeRunProgress);
                    syncPair.CurrentProgressValue = CalculateRunProgressValue(activeRunProgress);
                }
                else
                {
                    bool hasAggregateProgress = TryCalculateAggregateTransferProgressValue(
                        syncPair.Id,
                        out double aggregateProgressValue);
                    syncPair.IsCurrentProgressIndeterminate = !hasAggregateProgress;
                    syncPair.CurrentProgressValue = hasAggregateProgress ? aggregateProgressValue : 0;
                }

                return;
            }

            if (_runProgressByPair.TryGetValue(syncPair.Id, out DesktopRunProgressSnapshot? runProgress))
            {
                syncPair.CurrentOperation = CreateRunProgressOperation(runProgress);
                syncPair.HasCurrentProgress = true;
                syncPair.IsCurrentProgressIndeterminate = IsIndeterminateRunProgress(runProgress);
                syncPair.CurrentProgressValue = CalculateRunProgressValue(runProgress);
                return;
            }

            ClearSyncPairProgress(syncPair);
        }

        private void RefreshCurrentTransferSummary()
        {
            DesktopTransferProgressSnapshot? latestProgress = _transferProgressByKey.Values
                .OrderByDescending(static progress => progress.OccurredAtUtc)
                .FirstOrDefault();
            if (latestProgress is null)
            {
                HasCurrentTransfer = false;
                IsCurrentTransferIndeterminate = false;
                CurrentTransferProgressValue = 0;
                CurrentTransferTitle = string.Empty;
                CurrentTransferDetails = string.Empty;
                _transferSyncPairId = null;
                _transferDirection = SyncTransferDirection.Unknown;
                _transferRelativePath = string.Empty;
                RaiseCurrentWorkProgressProperties();
                return;
            }

            SyncPairRowViewModel? syncPair = SyncPairs.FirstOrDefault(pair => pair.Id == latestProgress.SyncPairId);
            if (syncPair is null)
            {
                _transferProgressByKey.Remove(CreateTransferProgressKey(latestProgress));
                RefreshCurrentTransferSummary();
                return;
            }

            SetCurrentTransferSummary(latestProgress, syncPair);
        }

        private void SetCurrentTransferSummary(
            DesktopTransferProgressSnapshot progress,
            SyncPairRowViewModel syncPair)
        {
            _transferSyncPairId = progress.SyncPairId;
            _transferDirection = progress.Direction;
            _transferRelativePath = progress.RelativePath;
            HasCurrentTransfer = true;
            IsCurrentTransferIndeterminate = !progress.TotalBytes.HasValue;
            CurrentTransferProgressValue = CalculateProgressValue(progress);
            CurrentTransferTitle = CreateTransferTitle(progress, syncPair.DisplayName);
            CurrentTransferDetails = CreateTransferDetails(progress);
        }

        private string CreateActiveTransferTitle()
        {
            DesktopTransferProgressSnapshot[] transfers = _transferProgressByKey.Values.ToArray();
            if (transfers.Length <= 1)
            {
                return CurrentTransferTitle;
            }

            string action = CreateAggregateTransferAction(transfers);
            string title = action
                + " "
                + transfers.Length.ToString(CultureInfo.CurrentCulture)
                + " files";
            Guid[] syncPairIds = transfers
                .Select(static transfer => transfer.SyncPairId)
                .Distinct()
                .ToArray();
            if (syncPairIds.Length == 1)
            {
                string? syncPairName = SyncPairs
                    .FirstOrDefault(pair => pair.Id == syncPairIds[0])
                    ?.DisplayName;
                return string.IsNullOrWhiteSpace(syncPairName) ? title : syncPairName + ": " + title;
            }

            return title
                + " across "
                + syncPairIds.Length.ToString(CultureInfo.CurrentCulture)
                + " folders";
        }

        private string CreateActiveTransferDetails()
        {
            return _transferProgressByKey.Count <= 1
                ? CurrentTransferDetails
                : CreateAggregateTransferDetails(_transferProgressByKey.Values, includeEstimatedTimeRemaining: true);
        }

        private string CreateSyncPairTransferOperation(
            Guid syncPairId,
            DesktopTransferProgressSnapshot latestTransfer)
        {
            DesktopTransferProgressSnapshot[] transfers = _transferProgressByKey.Values
                .Where(transfer => transfer.SyncPairId == syncPairId)
                .ToArray();
            if (transfers.Length <= 1)
            {
                return CreateTransferOperation(latestTransfer);
            }

            return CreateAggregateTransferAction(transfers)
                + " "
                + transfers.Length.ToString(CultureInfo.CurrentCulture)
                + " files";
        }

        private static string CreateAggregateTransferAction(
            IReadOnlyList<DesktopTransferProgressSnapshot> transfers)
        {
            SyncTransferDirection direction = transfers[0].Direction;
            return transfers.All(transfer => transfer.Direction == direction)
                ? CreateTransferAction(direction, isCompleted: false)
                : "Syncing";
        }

        private DesktopTransferProgressSnapshot? GetLatestActiveTransferForPair(Guid syncPairId)
        {
            return _transferProgressByKey.Values
                .Where(progress => progress.SyncPairId == syncPairId)
                .OrderByDescending(static progress => progress.OccurredAtUtc)
                .FirstOrDefault();
        }

        private bool HasActiveTransferForPair(Guid syncPairId)
        {
            return _transferProgressByKey.Keys.Any(key => key.SyncPairId == syncPairId);
        }

        private bool RemoveTransferProgressForPair(Guid syncPairId)
        {
            RunTransferProgressKey[] keys = _transferProgressByKey.Keys
                .Where(key => key.SyncPairId == syncPairId)
                .ToArray();
            foreach (RunTransferProgressKey key in keys)
            {
                _transferProgressByKey.Remove(key);
            }

            return keys.Length > 0;
        }
    }
}
