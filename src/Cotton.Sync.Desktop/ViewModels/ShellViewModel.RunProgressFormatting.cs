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
        private double GetDisplayedRunProgressUnits(DesktopRunProgressSnapshot progress)
        {
            if (progress.FilesTotal is not > 0)
            {
                return progress.IsCompleted ? 1 : 0;
            }

            int total = progress.FilesTotal.Value;
            double completed = Math.Clamp(progress.FilesCompleted, 0, total);
            if (!progress.IsCompleted && IsCountedRunStage(progress.Stage))
            {
                double activeTransferFiles = 0;
                foreach (DesktopTransferProgressSnapshot transfer in _transferProgressByKey.Values)
                {
                    if (transfer.SyncPairId != progress.SyncPairId || transfer.TotalBytes is not > 0)
                    {
                        continue;
                    }

                    double transferred = Math.Clamp(transfer.TransferredBytes, 0, transfer.TotalBytes.Value);
                    activeTransferFiles += transferred / transfer.TotalBytes.Value;
                }

                if (activeTransferFiles > 0)
                {
                    return Math.Clamp(completed + activeTransferFiles, 0, total);
                }
            }

            return GetDisplayedRunProgressCount(progress);
        }

        private static bool IsRunTransferDirection(SyncTransferDirection direction)
        {
            return direction is SyncTransferDirection.Upload or SyncTransferDirection.Download;
        }

        private bool TryGetRunTransferSpeed(out double bytesPerSecond)
        {
            if (_runTransferSpeedBytesPerSecond is > 0)
            {
                bytesPerSecond = _runTransferSpeedBytesPerSecond.Value;
                return true;
            }

            bytesPerSecond = 0;
            return false;
        }

        private bool TryCalculateAggregateTransferProgressValue(out double progressValue)
        {
            return TryCalculateAggregateTransferProgressValue(syncPairId: null, out progressValue);
        }

        private bool TryCalculateAggregateTransferProgressValue(Guid syncPairId, out double progressValue)
        {
            return TryCalculateAggregateTransferProgressValue((Guid?)syncPairId, out progressValue);
        }

        private bool TryCalculateAggregateTransferProgressValue(Guid? syncPairId, out double progressValue)
        {
            progressValue = 0;
            int transferCount = 0;
            long transferredBytes = 0;
            long totalBytes = 0;
            foreach (DesktopTransferProgressSnapshot progress in _transferProgressByKey.Values)
            {
                if (syncPairId.HasValue && progress.SyncPairId != syncPairId.Value)
                {
                    continue;
                }

                transferCount++;
                if (progress.TotalBytes is not > 0)
                {
                    return false;
                }

                totalBytes += progress.TotalBytes.Value;
                transferredBytes += Math.Clamp(progress.TransferredBytes, 0, progress.TotalBytes.Value);
            }

            if (transferCount == 0 || totalBytes <= 0)
            {
                return false;
            }

            progressValue = Math.Clamp((double)transferredBytes / totalBytes * 100, 0, 100);
            return true;
        }

        private static string CreateAggregateRunProgressDetails(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            (int CompletedFiles, int TotalFiles, bool HasUnknownTotals) counts =
                CalculateAggregateRunProgressCounts(progressValues);
            if (counts.HasUnknownTotals)
            {
                return CreateUnknownAggregateRunProgressDetails(progressValues, counts.CompletedFiles);
            }

            if (counts.TotalFiles > 0 && counts.CompletedFiles == 0)
            {
                return ResolveAggregatePreparationLabel(progressValues)
                    + " across "
                    + progressValues.Count.ToString(CultureInfo.CurrentCulture)
                    + " folders";
            }

            return counts.CompletedFiles.ToString(CultureInfo.CurrentCulture)
                + " of "
                + counts.TotalFiles.ToString(CultureInfo.CurrentCulture)
                + " "
                + ResolveAggregateProgressUnit(progressValues)
                + " across "
                + progressValues.Count.ToString(CultureInfo.CurrentCulture)
                + " folders";
        }

        private static (int CompletedFiles, int TotalFiles, bool HasUnknownTotals)
            CalculateAggregateRunProgressCounts(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            int completedFiles = 0;
            int totalFiles = 0;
            bool hasUnknownTotals = false;
            foreach (DesktopRunProgressSnapshot progress in progressValues)
            {
                if (!progress.FilesTotal.HasValue)
                {
                    hasUnknownTotals = true;
                    completedFiles += GetDisplayedRunProgressCount(progress);
                    continue;
                }

                completedFiles += GetDisplayedRunProgressCount(progress);
                totalFiles += progress.FilesTotal.Value;
            }

            return (completedFiles, totalFiles, hasUnknownTotals);
        }

        private static string CreateUnknownAggregateRunProgressDetails(
            IReadOnlyList<DesktopRunProgressSnapshot> progressValues,
            int completedFiles)
        {
            if (completedFiles > 0
                && progressValues.All(static progress => progress.Stage == SyncRunProgressStage.ScanningLocal))
            {
                return completedFiles.ToString(CultureInfo.CurrentCulture)
                    + (completedFiles == 1 ? " file found across " : " files found across ")
                    + progressValues.Count.ToString(CultureInfo.CurrentCulture)
                    + " folders";
            }

            if (completedFiles > 0
                && progressValues.All(static progress => progress.Stage == SyncRunProgressStage.ScanningRemote))
            {
                return completedFiles.ToString(CultureInfo.CurrentCulture)
                    + (completedFiles == 1 ? " cloud file found across " : " cloud files found across ")
                    + progressValues.Count.ToString(CultureInfo.CurrentCulture)
                    + " folders";
            }

            return progressValues.Count.ToString(CultureInfo.CurrentCulture) + " folders are syncing.";
        }

        private static string ResolveAggregatePreparationLabel(
            IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.ReconcilingDirectories))
            {
                return "Preparing folders";
            }

            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.ReconcilingFiles))
            {
                return "Preparing file checks";
            }

            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders))
            {
                return VirtualFileUserFacingCopy.PreparingCloudFilesProgressLabel;
            }

            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.HydratingCloudFiles))
            {
                return "Preparing files";
            }

            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.DehydratingCloudFiles))
            {
                return "Preparing to free up space";
            }

            return "Preparing sync";
        }

        private static string ResolveAggregateProgressUnit(
            IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders))
            {
                return VirtualFileUserFacingCopy.CloudFilesProgressUnit;
            }

            if (progressValues.All(static progress => progress.Stage == SyncRunProgressStage.ScanningRemote))
            {
                return "cloud items";
            }

            return progressValues.All(static progress => progress.Stage == SyncRunProgressStage.FinalizingCloudFiles)
                ? "folders"
                : "files";
        }

        private static string CreateSingleRunProgressDetails(DesktopRunProgressSnapshot progress)
        {
            string label = GetRunStageLabel(progress.Stage);
            string details = CreateRunProgressDetails(progress);
            string stageDetails;
            if (string.IsNullOrWhiteSpace(details))
            {
                stageDetails = label;
            }
            else if (!progress.FilesTotal.HasValue && progress.FilesCompleted <= 0)
            {
                stageDetails = details;
            }
            else if (IsStartingCountedRunProgress(progress))
            {
                stageDetails = details;
            }
            else
            {
                stageDetails = label + " · " + details;
            }

            string context = CreateRunContextDetails(progress);
            return string.IsNullOrWhiteSpace(context)
                ? stageDetails
                : stageDetails + " · " + context;
        }

        private static string CreateRunContextDetails(DesktopRunProgressSnapshot progress)
        {
            if (progress.Causes == SyncRunCause.InternalMaintenance)
            {
                return string.Empty;
            }

            string cause = GetRunCauseLabel(progress.Causes);
            string scope = progress.IsFull
                ? "full folder scope"
                : progress.RequestedPathCount == 1
                    ? "1 changed path"
                    : progress.RequestedPathCount.ToString(CultureInfo.CurrentCulture) + " changed paths";
            return cause + " · " + scope;
        }

        private static string GetRunCauseLabel(SyncRunCause causes)
        {
            if ((causes & SyncRunCause.RemoteCursorExpired) != 0)
            {
                return "Recovering missed cloud changes";
            }

            if ((causes & SyncRunCause.LocalWatcherError) != 0)
            {
                return "Recovering local change tracking";
            }

            if ((causes & SyncRunCause.LocalChangeOverflow) != 0)
            {
                return "Recovering a local change burst";
            }

            if ((causes & SyncRunCause.LocalRenameRecovery) != 0)
            {
                return "Recovering a local rename";
            }

            bool hasLocalChange = (causes & SyncRunCause.LocalChange) != 0;
            bool hasRemoteChange = (causes & SyncRunCause.RealtimeRemoteChange) != 0;
            if (hasLocalChange && hasRemoteChange)
            {
                return "Local and cloud changes";
            }

            if (hasLocalChange)
            {
                return "Local change";
            }

            if (hasRemoteChange)
            {
                return "Cloud change";
            }

            if ((causes & SyncRunCause.InitialPopulation) != 0)
            {
                return "Initial sync";
            }

            if ((causes & SyncRunCause.Resume) != 0)
            {
                return "Resume check";
            }

            if ((causes & SyncRunCause.Periodic) != 0)
            {
                return "Scheduled check";
            }

            return "Manual refresh";
        }

        private static string CreateRunProgressOperation(DesktopRunProgressSnapshot progress)
        {
            string label = GetRunOperationLabel(progress.Stage);
            if (!progress.IsCompleted && progress.Stage == SyncRunProgressStage.CreatingPlaceholders)
            {
                return label;
            }

            if (IsStartingCountedRunProgress(progress))
            {
                return GetStartingRunProgressOperationLabel(progress.Stage);
            }

            if (IsOpenEndedPlaceholderCreation(progress) && progress.FilesCompleted > 0)
            {
                return label;
            }

            if (progress.FilesTotal.HasValue && IsCountedRunStage(progress.Stage))
            {
                return label + " " + GetDisplayedRunProgressCount(progress).ToString(CultureInfo.CurrentCulture)
                    + " of " + progress.FilesTotal.Value.ToString(CultureInfo.CurrentCulture);
            }

            return label;
        }
    }
}
