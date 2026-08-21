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
        private static string CreateRunProgressDetails(DesktopRunProgressSnapshot progress)
        {
            if (progress.FilesTotal.HasValue)
            {
                if (IsOpenEndedPlaceholderCreation(progress))
                {
                    int readyCount = GetDisplayedRunProgressCount(progress);
                    if (readyCount <= 0)
                    {
                        return PreparingCloudFilesProgressLabel
                            + " \u00B7 scanning cloud \u00B7 creating placeholders \u00B7 saving state";
                    }

                    return readyCount.ToString(CultureInfo.CurrentCulture)
                        + (readyCount == 1 ? " cloud item ready" : " cloud items ready")
                        + " \u00B7 scanning cloud \u00B7 saving state";
                }

                if (IsStartingCountedRunProgress(progress))
                {
                    int total = progress.FilesTotal.Value;
                    string queuedUnitName = GetRunProgressUnitName(progress.Stage, total, total);
                    return GetStartingRunProgressLabel(progress.Stage)
                        + " · "
                        + total.ToString(CultureInfo.CurrentCulture)
                        + " "
                        + queuedUnitName
                        + " queued";
                }

                int displayCount = GetDisplayedRunProgressCount(progress);
                string unitName = GetRunProgressUnitName(progress.Stage, displayCount, progress.FilesTotal.Value);
                string details = displayCount.ToString(CultureInfo.CurrentCulture)
                    + " of "
                    + progress.FilesTotal.Value.ToString(CultureInfo.CurrentCulture)
                    + " "
                    + unitName;
                return IsCountedRunStage(progress.Stage) || string.IsNullOrWhiteSpace(progress.CurrentPath)
                    ? details
                    : details + " · " + GetDisplayFileName(progress.CurrentPath);
            }

            return progress.Stage switch
            {
                SyncRunProgressStage.ScanningLocal => CreateLocalScanProgressDetails(progress),
                SyncRunProgressStage.ScanningRemote => CreateRemoteScanProgressDetails(progress),
                SyncRunProgressStage.ReconcilingDirectories => "Preparing folders.",
                SyncRunProgressStage.CreatingPlaceholders => PreparingCloudFilesProgressLabel + ".",
                SyncRunProgressStage.FinalizingCloudFiles => "Finalizing cloud file status.",
                SyncRunProgressStage.HydratingCloudFiles => "Making files available.",
                SyncRunProgressStage.DehydratingCloudFiles => "Freeing up space.",
                SyncRunProgressStage.Completed => "Sync pass completed.",
                _ => "Preparing sync.",
            };
        }

        private static string CreateLocalScanProgressDetails(DesktopRunProgressSnapshot progress)
        {
            if (progress.FilesCompleted <= 0)
            {
                if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
                {
                    return "Looking for local changes · " + GetDisplayFileName(progress.CurrentPath);
                }

                return "Looking for local changes.";
            }

            string details = progress.FilesCompleted.ToString(CultureInfo.CurrentCulture)
                + (progress.FilesCompleted == 1 ? " file found" : " files found");
            if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
            {
                details += " · " + GetDisplayFileName(progress.CurrentPath);
            }

            return details;
        }

        private static bool IsCountedRunStage(SyncRunProgressStage stage)
        {
            return stage == SyncRunProgressStage.ReconcilingDirectories
                || stage == SyncRunProgressStage.ReconcilingFiles
                || stage == SyncRunProgressStage.CreatingPlaceholders
                || stage == SyncRunProgressStage.FinalizingCloudFiles
                || stage == SyncRunProgressStage.HydratingCloudFiles
                || stage == SyncRunProgressStage.DehydratingCloudFiles;
        }

        private static bool IsIndeterminateRunProgress(DesktopRunProgressSnapshot progress)
        {
            return (!progress.FilesTotal.HasValue && !progress.IsCompleted)
                || IsIndeterminatePlaceholderCreation(progress)
                || IsStartingCountedRunProgress(progress);
        }

        private static bool IsIndeterminatePlaceholderCreation(DesktopRunProgressSnapshot progress)
        {
            return progress.Stage == SyncRunProgressStage.CreatingPlaceholders
                && !progress.IsCompleted;
        }

        private static bool IsOpenEndedPlaceholderCreation(DesktopRunProgressSnapshot progress)
        {
            return !progress.IsCompleted
                && progress.Stage == SyncRunProgressStage.CreatingPlaceholders;
        }

        private static bool IsStartingCountedRunProgress(DesktopRunProgressSnapshot progress)
        {
            return !progress.IsCompleted
                && IsCountedRunStage(progress.Stage)
                && progress.FilesTotal is > 0
                && progress.FilesCompleted == 0
                && (string.IsNullOrWhiteSpace(progress.CurrentPath)
                    || progress.Stage == SyncRunProgressStage.CreatingPlaceholders);
        }

        private static int GetDisplayedRunProgressCount(DesktopRunProgressSnapshot progress)
        {
            if (progress.Stage == SyncRunProgressStage.CreatingPlaceholders
                && progress.FilesCompleted == 0)
            {
                return 0;
            }

            if (!progress.IsCompleted
                && IsCountedRunStage(progress.Stage)
                && progress.FilesTotal is > 0
                && progress.FilesCompleted == 0
                && !string.IsNullOrWhiteSpace(progress.CurrentPath))
            {
                return 1;
            }

            return progress.FilesCompleted;
        }

        private static string GetRunProgressUnitName(SyncRunProgressStage stage, int completed, int total)
        {
            bool singular = completed == 1 && total == 1;
            if (stage == SyncRunProgressStage.ReconcilingDirectories)
            {
                return singular ? "folder" : "folders";
            }

            if (stage == SyncRunProgressStage.CreatingPlaceholders)
            {
                return VirtualFileUserFacingCopy.CloudItemsProgressUnit;
            }

            if (stage == SyncRunProgressStage.ScanningRemote)
            {
                return singular ? "cloud item" : "cloud items";
            }

            if (stage == SyncRunProgressStage.FinalizingCloudFiles)
            {
                return singular ? "folder" : "folders";
            }

            return singular ? "file" : "files";
        }

        private static string CreateRemoteScanProgressDetails(DesktopRunProgressSnapshot progress)
        {
            if (progress.FilesCompleted <= 0)
            {
                if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
                {
                    return "Checking Cotton Cloud · " + GetDisplayFileName(progress.CurrentPath);
                }

                return "Checking Cotton Cloud.";
            }

            string details = progress.FilesCompleted.ToString(CultureInfo.CurrentCulture)
                + (progress.FilesCompleted == 1 ? " cloud file found" : " cloud files found");
            if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
            {
                details += " · " + GetDisplayFileName(progress.CurrentPath);
            }

            return details;
        }

        private static string GetRunStageLabel(SyncRunProgressStage stage)
        {
            return stage switch
            {
                SyncRunProgressStage.ScanningLocal => "Scanning local files",
                SyncRunProgressStage.ScanningRemote => "Scanning Cotton Cloud",
                SyncRunProgressStage.ReconcilingDirectories => "Preparing folders",
                SyncRunProgressStage.ReconcilingFiles => "Checking files",
                SyncRunProgressStage.CreatingPlaceholders => CreatingCloudFilesProgressLabel,
                SyncRunProgressStage.FinalizingCloudFiles => "Finalizing cloud file status",
                SyncRunProgressStage.HydratingCloudFiles => "Making files available",
                SyncRunProgressStage.DehydratingCloudFiles => "Freeing up space",
                SyncRunProgressStage.Completed => "Finishing sync",
                _ => "Syncing",
            };
        }

        private static string GetRunOperationLabel(SyncRunProgressStage stage)
        {
            return stage switch
            {
                SyncRunProgressStage.ScanningRemote => RemoteScanRowProgressLabel,
                SyncRunProgressStage.CreatingPlaceholders => PreparingCloudFilesProgressLabel,
                _ => GetRunStageLabel(stage),
            };
        }

        private static string GetStartingRunProgressLabel(SyncRunProgressStage stage)
        {
            return stage switch
            {
                SyncRunProgressStage.ReconcilingDirectories => "Preparing folders",
                SyncRunProgressStage.ReconcilingFiles => "Preparing file checks",
                SyncRunProgressStage.CreatingPlaceholders => PreparingCloudFilesProgressLabel,
                SyncRunProgressStage.FinalizingCloudFiles => "Finalizing cloud file status",
                SyncRunProgressStage.HydratingCloudFiles => "Preparing files",
                SyncRunProgressStage.DehydratingCloudFiles => "Preparing to free up space",
                _ => "Preparing sync",
            };
        }

        private static string GetStartingRunProgressOperationLabel(SyncRunProgressStage stage)
        {
            return stage == SyncRunProgressStage.CreatingPlaceholders
                ? PreparingCloudFilesProgressLabel
                : GetStartingRunProgressLabel(stage);
        }

        private static string CreateTransferTitle(DesktopTransferProgressSnapshot progress, string syncPairName)
        {
            string action = CreateTransferAction(progress.Direction, progress.IsCompleted);
            return syncPairName + ": " + action + " " + GetDisplayFileName(progress.RelativePath);
        }

        private static string CreateTransferOperation(DesktopTransferProgressSnapshot progress)
        {
            string action = CreateTransferAction(progress.Direction, isCompleted: false);
            return action + " " + GetDisplayFileName(progress.RelativePath);
        }

        private static string CreateTransferAction(SyncTransferDirection direction, bool isCompleted)
        {
            return direction switch
            {
                SyncTransferDirection.Upload => isCompleted ? "Uploaded" : "Uploading",
                SyncTransferDirection.Download => isCompleted ? "Downloaded" : "Downloading",
                SyncTransferDirection.Hash => isCompleted ? "Checked" : "Checking",
                _ => isCompleted ? "Synced" : "Syncing",
            };
        }

        private static string CreateTransferDetails(DesktopTransferProgressSnapshot progress)
        {
            string size = progress.TotalBytes.HasValue
                ? FormatBytes(progress.TransferredBytes) + " / " + FormatBytes(progress.TotalBytes.Value)
                : FormatBytes(progress.TransferredBytes);
            double? bytesPerSecond = progress.SpeedBytesPerSecond;
            if (!bytesPerSecond.HasValue || bytesPerSecond.Value <= 0 || progress.IsCompleted)
            {
                return size;
            }

            string details = size + " · " + FormatBytes(bytesPerSecond.Value) + "/s";
            if (progress.EstimatedTimeRemaining.HasValue)
            {
                details += " · " + FormatDuration(progress.EstimatedTimeRemaining.Value) + " left";
            }

            return details;
        }

        private static string CreateAggregateTransferDetails(
            IEnumerable<DesktopTransferProgressSnapshot> progressValues,
            bool includeEstimatedTimeRemaining)
        {
            TransferMetricDetails details = CreateAggregateTransferMetricDetails(
                progressValues,
                includeEstimatedTimeRemaining);
            return string.IsNullOrWhiteSpace(details.Rate) ? details.Size : details.Size + " · " + details.Rate;
        }

        private static string CreateHeaderDetails(string size, string rate)
        {
            if (string.IsNullOrWhiteSpace(size))
            {
                return rate;
            }

            return string.IsNullOrWhiteSpace(rate) ? size : size + " · " + rate;
        }

        private static TransferMetricDetails CreateAggregateTransferMetricDetails(
            IEnumerable<DesktopTransferProgressSnapshot> progressValues,
            bool includeEstimatedTimeRemaining = true)
        {
            (
                long transferredBytes,
                long totalBytes,
                bool hasTotalBytes,
                double speedBytesPerSecond,
                TimeSpan? longestEstimatedTimeRemaining) = AggregateTransferMetrics(progressValues);
            string details = hasTotalBytes
                ? FormatBytes(transferredBytes) + " / " + FormatBytes(totalBytes)
                : FormatBytes(transferredBytes);
            if (speedBytesPerSecond <= 0)
            {
                return new TransferMetricDetails(details, string.Empty);
            }

            string rate = FormatBytes(speedBytesPerSecond) + "/s";
            if (includeEstimatedTimeRemaining && longestEstimatedTimeRemaining.HasValue)
            {
                rate += " · " + FormatDuration(longestEstimatedTimeRemaining.Value) + " left";
            }

            return new TransferMetricDetails(details, rate);
        }
    }
}
