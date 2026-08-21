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
        private static (long TransferredBytes, long TotalBytes, bool HasTotalBytes,
            double SpeedBytesPerSecond, TimeSpan? LongestEstimatedTimeRemaining) AggregateTransferMetrics(
            IEnumerable<DesktopTransferProgressSnapshot> progressValues)
        {
            long transferredBytes = 0;
            long totalBytes = 0;
            bool hasTotalBytes = true;
            double speedBytesPerSecond = 0;
            TimeSpan? longestEstimatedTimeRemaining = null;
            foreach (DesktopTransferProgressSnapshot progress in progressValues)
            {
                transferredBytes += progress.TransferredBytes;
                (totalBytes, hasTotalBytes) = AccumulateTotalBytes(progress, totalBytes, hasTotalBytes);
                speedBytesPerSecond += GetPositiveTransferSpeed(progress);
                TimeSpan? estimatedTimeRemaining = GetTransferEstimatedTimeRemaining(progress);
                if (estimatedTimeRemaining.HasValue
                    && (!longestEstimatedTimeRemaining.HasValue
                        || estimatedTimeRemaining.Value > longestEstimatedTimeRemaining.Value))
                {
                    longestEstimatedTimeRemaining = estimatedTimeRemaining;
                }
            }

            return (transferredBytes, totalBytes, hasTotalBytes, speedBytesPerSecond, longestEstimatedTimeRemaining);
        }

        private static (long TotalBytes, bool HasTotalBytes) AccumulateTotalBytes(
            DesktopTransferProgressSnapshot progress,
            long currentTotalBytes,
            bool hasTotalBytes)
        {
            if (!progress.TotalBytes.HasValue)
            {
                return (currentTotalBytes, false);
            }

            return (currentTotalBytes + progress.TotalBytes.Value, hasTotalBytes);
        }

        private static double GetPositiveTransferSpeed(DesktopTransferProgressSnapshot progress)
        {
            return progress.SpeedBytesPerSecond is > 0 ? progress.SpeedBytesPerSecond.Value : 0;
        }

        private static TimeSpan? GetTransferEstimatedTimeRemaining(DesktopTransferProgressSnapshot progress)
        {
            if (progress.SpeedBytesPerSecond is not > 0
                || progress.TotalBytes is not > 0
                || progress.TotalBytes.Value <= progress.TransferredBytes)
            {
                return null;
            }

            return progress.EstimatedTimeRemaining
                ?? TimeSpan.FromSeconds(
                    (progress.TotalBytes.Value - progress.TransferredBytes) / progress.SpeedBytesPerSecond.Value);
        }

        private static string GetDisplayFileName(string relativePath)
        {
            string normalized = relativePath.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "item";
            }

            int separatorIndex = normalized.LastIndexOf('/');
            return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            string format = unitIndex == 0 || value >= 10 ? "0" : "0.0";
            return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unitIndex];
        }

        private static string FormatUpdateDownloadProgress(DesktopUpdateDownloadProgress progress)
        {
            if (progress.TotalBytes is > 0)
            {
                double percent = Math.Clamp(progress.BytesDownloaded / (double)progress.TotalBytes.Value * 100d, 0d, 100d);
                return "Downloading "
                    + FormatBytes(progress.BytesDownloaded)
                    + " / "
                    + FormatBytes(progress.TotalBytes.Value)
                    + " ("
                    + percent.ToString("0", CultureInfo.CurrentCulture)
                    + "%).";
            }

            return "Downloading " + FormatBytes(progress.BytesDownloaded) + ".";
        }

        private static string FormatFileRate(double filesPerSecond)
        {
            return FormatUnitRate(filesPerSecond, " file/s", " files/s");
        }

        private static string FormatCloudItemRate(double itemsPerSecond)
        {
            return FormatUnitRate(itemsPerSecond, " cloud item/s", " cloud items/s");
        }

        private static string FormatFolderRate(double foldersPerSecond)
        {
            return FormatUnitRate(foldersPerSecond, " folder/s", " folders/s");
        }

        private static string FormatUnitRate(double unitsPerSecond, string singularUnit, string pluralUnit)
        {
            double roundedValue = unitsPerSecond >= 10
                ? Math.Round(unitsPerSecond)
                : Math.Round(unitsPerSecond, 1);
            string format = roundedValue >= 10 || Math.Abs(roundedValue - Math.Round(roundedValue)) < 0.05
                ? "0"
                : "0.0";
            string unit = Math.Abs(roundedValue - 1) < 0.05 ? singularUnit : pluralUnit;
            return roundedValue.ToString(format, CultureInfo.CurrentCulture) + unit;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 60)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
                if (seconds >= 10)
                {
                    seconds = RoundUp(seconds, 5);
                }

                return seconds.ToString(CultureInfo.CurrentCulture) + "s";
            }

            if (duration.TotalMinutes < 60)
            {
                int totalSeconds = RoundUp(Math.Max(60, (int)Math.Ceiling(duration.TotalSeconds)), 5);
                return (totalSeconds / 60).ToString(CultureInfo.CurrentCulture)
                    + "m "
                    + (totalSeconds % 60).ToString("00", CultureInfo.CurrentCulture)
                    + "s";
            }

            int totalMinutes = RoundUp(Math.Max(60, (int)Math.Ceiling(duration.TotalMinutes)), 5);
            return (totalMinutes / 60).ToString(CultureInfo.CurrentCulture)
                + "h "
                + (totalMinutes % 60).ToString("00", CultureInfo.CurrentCulture)
                + "m";
        }

        private static int RoundUp(int value, int step)
        {
            return ((value + step - 1) / step) * step;
        }

        private static double CalculateExponentialSmoothingFactor(TimeSpan elapsed, TimeSpan timeConstant)
        {
            if (elapsed <= TimeSpan.Zero)
            {
                return 0;
            }

            return 1 - Math.Exp(-elapsed.TotalSeconds / timeConstant.TotalSeconds);
        }

        private static bool IsActiveProgressPair(SyncPairRowViewModel syncPair)
        {
            return !string.IsNullOrWhiteSpace(syncPair.CurrentOperation)
                || string.Equals(syncPair.Status, "Scanning", StringComparison.Ordinal)
                || string.Equals(syncPair.Status, "Syncing", StringComparison.Ordinal)
                || string.Equals(syncPair.Status, "Sync requested", StringComparison.Ordinal)
                || string.Equals(syncPair.Status, "Pausing", StringComparison.Ordinal);
        }

        private SyncPairRowViewModel? ResolveConflictSyncPair(ConflictRowViewModel conflict)
        {
            return conflict.SyncPairId is { } syncPairId
                ? SyncPairs.FirstOrDefault(syncPair => syncPair.Id == syncPairId)
                : SelectedSyncPair;
        }

        private static string ResolveConflictOpenPath(string localRootPath, string relativePath)
        {
            string localRoot = Path.GetFullPath(localRootPath.Trim());
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return localRoot;
            }

            string normalizedRelativePath = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string combinedPath = Path.GetFullPath(Path.Combine(localRoot, normalizedRelativePath));
            if (!IsPathInsideRoot(localRoot, combinedPath))
            {
                return localRoot;
            }

            if (Directory.Exists(combinedPath))
            {
                return combinedPath;
            }

            string? parentPath = Path.GetDirectoryName(combinedPath);
            return string.IsNullOrWhiteSpace(parentPath) || !IsPathInsideRoot(localRoot, parentPath)
                ? localRoot
                : parentPath;
        }

        private static bool IsPathInsideRoot(string localRootPath, string path)
        {
            string root = Path.GetFullPath(localRootPath);
            string candidate = Path.GetFullPath(path);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(root, candidate, comparison)
                || candidate.StartsWith(EnsureTrailingSeparator(root), comparison);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static SyncPairRowViewModel ToRow(SyncPairSettings syncPair)
        {
            return new SyncPairRowViewModel
            {
                Id = syncPair.Id,
                IsEnabled = syncPair.IsEnabled,
                DisplayName = syncPair.DisplayName,
                EditableDisplayName = syncPair.DisplayName,
                LocalPath = syncPair.LocalRootPath,
                Mode = syncPair.Mode,
                RemoteRootNodeId = syncPair.RemoteRootNodeId,
                RemotePath = syncPair.RemoteDisplayPath,
                Status = syncPair.IsEnabled ? "Idle" : "Disabled",
            };
        }

        private static SyncPairRowViewModel ToRow(DesktopSyncPairSnapshot syncPair)
        {
            return new SyncPairRowViewModel
            {
                Id = syncPair.Id,
                IsEnabled = !string.Equals(syncPair.Status, "Disabled", StringComparison.Ordinal),
                DisplayName = syncPair.DisplayName,
                EditableDisplayName = syncPair.DisplayName,
                LocalPath = syncPair.LocalPath,
                Mode = syncPair.Mode,
                RemoteRootNodeId = syncPair.RemoteRootNodeId,
                RemotePath = syncPair.RemotePath,
                Status = syncPair.Status,
                LastSyncedAtUtc = syncPair.LastSyncedAtUtc,
                ChangeCursor = syncPair.ChangeCursor,
                LastError = syncPair.LastError,
            };
        }

        private static SyncPairSettings ToSettingsForValidation(SyncPairRowViewModel syncPair)
        {
            Guid remoteRootNodeId = syncPair.RemoteRootNodeId is { } value && value != Guid.Empty
                ? value
                : Guid.NewGuid();
            return new SyncPairSettings
            {
                Id = syncPair.Id,
                DisplayName = syncPair.DisplayName,
                LocalRootPath = syncPair.LocalPath,
                RemoteRootNodeId = remoteRootNodeId,
                RemoteDisplayPath = string.IsNullOrWhiteSpace(syncPair.RemotePath) ? "/" : syncPair.RemotePath,
                IsEnabled = syncPair.IsEnabled,
                Mode = syncPair.Mode,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static AppThemeMode NormalizeThemeModeIndex(int index)
        {
            AppThemeMode themeMode = (AppThemeMode)index;
            return Enum.IsDefined(themeMode) ? themeMode : AppThemeMode.System;
        }

        private static string ResolveAccountDisplayName(string? primary, string? fallback)
        {
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary.Trim();
            }

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback.Trim();
            }

            return "Cotton Sync";
        }
    }
}
