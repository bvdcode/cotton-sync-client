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
        private void UpdateRunTransferEstimatedTimeRemaining(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            if (!TryCalculateAggregateRunTransferBytes(progressValues, out long transferredBytes, out long totalBytes)
                || transferredBytes >= totalBytes)
            {
                _runTransferEstimatedTimeRemaining = null;
                _lastRunTransferEstimateOccurredAtUtc = null;
                return;
            }

            DateTime occurredAtUtc = GetLatestRunTransferEstimateOccurredAtUtc(progressValues);
            if (transferredBytes > _runTransferredBytes)
            {
                _runTransferredBytes = transferredBytes;
            }

            AddRunTransferSample(_runTransferredBytes, occurredAtUtc);
            if (!TryGetRunTransferSpeed(out double bytesPerSecond))
            {
                _runTransferEstimatedTimeRemaining = null;
                _lastRunTransferEstimateOccurredAtUtc = null;
                return;
            }

            TimeSpan rawEstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - transferredBytes) / bytesPerSecond);
            _runTransferEstimatedTimeRemaining = SmoothEstimatedTimeRemaining(
                rawEstimatedTimeRemaining,
                occurredAtUtc,
                _runTransferEstimatedTimeRemaining,
                _lastRunTransferEstimateOccurredAtUtc);
            _lastRunTransferEstimateOccurredAtUtc = occurredAtUtc;
        }

        private DateTime GetLatestRunTransferEstimateOccurredAtUtc(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            DateTime occurredAtUtc = _runTransferSamples.Count > 0
                ? _runTransferSamples.Last().OccurredAtUtc
                : DateTime.MinValue;
            foreach (DesktopRunProgressSnapshot progress in progressValues)
            {
                DateTime progressOccurredAtUtc = progress.OccurredAtUtc.ToUniversalTime();
                if (progressOccurredAtUtc > occurredAtUtc)
                {
                    occurredAtUtc = progressOccurredAtUtc;
                }
            }

            return occurredAtUtc;
        }

        private static TimeSpan SmoothEstimatedTimeRemaining(
            TimeSpan rawEstimate,
            DateTime occurredAtUtc,
            TimeSpan? previousEstimate,
            DateTime? previousOccurredAtUtc)
        {
            if (!previousEstimate.HasValue
                || !previousOccurredAtUtc.HasValue
                || occurredAtUtc <= previousOccurredAtUtc.Value)
            {
                return rawEstimate;
            }

            TimeSpan elapsed = occurredAtUtc - previousOccurredAtUtc.Value;
            TimeSpan agedPreviousEstimate = previousEstimate.Value - elapsed;
            if (agedPreviousEstimate < TimeSpan.Zero)
            {
                agedPreviousEstimate = TimeSpan.Zero;
            }

            double smoothingFactor = CalculateExponentialSmoothingFactor(elapsed, RunProgressEstimateSmoothingPeriod);
            double smoothedSeconds = agedPreviousEstimate.TotalSeconds
                + ((rawEstimate.TotalSeconds - agedPreviousEstimate.TotalSeconds) * smoothingFactor);
            return TimeSpan.FromSeconds(Math.Max(0, smoothedSeconds));
        }

        private bool TryCalculateAggregateRunProgressEstimate(
            IReadOnlyList<DesktopRunProgressSnapshot> progressValues,
            out double filesPerSecond,
            out double remainingFiles,
            out DateTime occurredAtUtc)
        {
            filesPerSecond = 0;
            remainingFiles = 0;
            occurredAtUtc = DateTime.MinValue;
            double completedFiles = 0;
            int totalFiles = 0;
            DateTime latestRunProgressAtUtc = DateTime.MinValue;
            foreach (DesktopRunProgressSnapshot progress in progressValues)
            {
                if (!IsCountedRunStage(progress.Stage) || progress.FilesTotal is not > 0)
                {
                    continue;
                }

                completedFiles += Math.Clamp(progress.FilesCompleted, 0, progress.FilesTotal.Value);
                totalFiles += progress.FilesTotal.Value;
                DateTime progressOccurredAtUtc = progress.OccurredAtUtc.ToUniversalTime();
                if (progressOccurredAtUtc > latestRunProgressAtUtc)
                {
                    latestRunProgressAtUtc = progressOccurredAtUtc;
                }
            }

            if (totalFiles <= 0 || completedFiles >= totalFiles || latestRunProgressAtUtc == DateTime.MinValue)
            {
                return false;
            }

            occurredAtUtc = latestRunProgressAtUtc;
            remainingFiles = totalFiles - completedFiles;
            if (!TryCalculateRunFileRate(totalFiles, completedFiles, occurredAtUtc, out filesPerSecond))
            {
                return false;
            }

            return remainingFiles > 0;
        }

        private void UpdateRunFileRate(double observedFilesPerSecond, DateTime occurredAtUtc)
        {
            if (!double.IsFinite(observedFilesPerSecond) || observedFilesPerSecond <= 0)
            {
                return;
            }

            if (!_currentRunProgressFilesPerSecond.HasValue
                || !_lastRunProgressFileRateOccurredAtUtc.HasValue
                || occurredAtUtc <= _lastRunProgressFileRateOccurredAtUtc.Value)
            {
                _currentRunProgressFilesPerSecond = observedFilesPerSecond;
                _lastRunProgressFileRateOccurredAtUtc = occurredAtUtc;
                return;
            }

            TimeSpan sampleElapsed = occurredAtUtc - _lastRunProgressFileRateOccurredAtUtc.Value;
            double smoothingFactor = CalculateExponentialSmoothingFactor(sampleElapsed, RunProgressEstimateSmoothingPeriod);
            _currentRunProgressFilesPerSecond = Math.Max(
                0,
                _currentRunProgressFilesPerSecond.Value
                    + ((observedFilesPerSecond - _currentRunProgressFilesPerSecond.Value) * smoothingFactor));
            _lastRunProgressFileRateOccurredAtUtc = occurredAtUtc;
        }

        private bool TryCalculateRunFileRate(
            int totalFiles,
            double completedFiles,
            DateTime occurredAtUtc,
            out double filesPerSecond)
        {
            filesPerSecond = 0;
            if (_runFileProgressSamples.Count > 0)
            {
                RunFileProgressSample lastSample = _runFileProgressSamples.Last();
                if (totalFiles < lastSample.TotalFiles
                    || completedFiles < lastSample.CompletedFiles
                    || occurredAtUtc - lastSample.OccurredAtUtc > RunTransferMetricsWindow)
                {
                    _runFileProgressSamples.Clear();
                }
                else if (completedFiles == lastSample.CompletedFiles)
                {
                    return TryCalculateRunFileRateFromSamples(out filesPerSecond);
                }
            }

            _runFileProgressSamples.Enqueue(new RunFileProgressSample(completedFiles, totalFiles, occurredAtUtc));
            PruneRunFileProgressSamples(occurredAtUtc);
            if (completedFiles < MinimumRunProgressEstimateCompletedFiles)
            {
                return false;
            }

            return TryCalculateRunFileRateFromSamples(out filesPerSecond);
        }

        private void PruneRunFileProgressSamples(DateTime occurredAtUtc)
        {
            while (_runFileProgressSamples.Count > 2
                && occurredAtUtc - _runFileProgressSamples.Peek().OccurredAtUtc > RunTransferMetricsWindow)
            {
                _runFileProgressSamples.Dequeue();
            }
        }

        private bool TryCalculateRunFileRateFromSamples(out double filesPerSecond)
        {
            filesPerSecond = 0;
            if (_runFileProgressSamples.Count < 2)
            {
                return false;
            }

            RunFileProgressSample firstSample = _runFileProgressSamples.Peek();
            RunFileProgressSample lastSample = _runFileProgressSamples.Last();
            if (lastSample.CompletedFiles < MinimumRunProgressEstimateCompletedFiles)
            {
                return false;
            }

            TimeSpan elapsed = lastSample.OccurredAtUtc - firstSample.OccurredAtUtc;
            double completedFiles = lastSample.CompletedFiles - firstSample.CompletedFiles;
            if (elapsed < MinimumRunProgressEstimateDuration || completedFiles <= 0)
            {
                return false;
            }

            filesPerSecond = completedFiles / elapsed.TotalSeconds;
            return double.IsFinite(filesPerSecond) && filesPerSecond > 0;
        }

        private static bool IsActiveSyncStatus(DesktopSyncPairStatusSnapshot status)
        {
            return string.Equals(status.Status, "Syncing", StringComparison.Ordinal)
                || string.Equals(status.Status, "Scanning", StringComparison.Ordinal);
        }

        private static double CalculateProgressValue(DesktopTransferProgressSnapshot progress)
        {
            if (progress.TotalBytes is > 0)
            {
                return Math.Clamp((double)progress.TransferredBytes / progress.TotalBytes.Value * 100, 0, 100);
            }

            return progress.IsCompleted ? 100 : 0;
        }

        private double CalculateRunProgressValue(DesktopRunProgressSnapshot progress)
        {
            if (TryCalculateRunTransferBytes(progress, out long transferredBytes, out long totalBytes))
            {
                return Math.Clamp((double)transferredBytes / totalBytes * 100, 0, 100);
            }

            if (progress.FilesTotal is > 0)
            {
                double displayCount = GetDisplayedRunProgressUnits(progress);
                return Math.Clamp(displayCount / progress.FilesTotal.Value * 100, 0, 100);
            }

            return progress.IsCompleted ? 100 : 0;
        }

        private double CalculateAggregateRunProgressValue(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            if (TryCalculateAggregateRunTransferBytes(progressValues, out long transferredBytes, out long totalBytes))
            {
                return Math.Clamp((double)transferredBytes / totalBytes * 100, 0, 100);
            }

            int totalFiles = 0;
            double completedFiles = 0;
            foreach (DesktopRunProgressSnapshot progress in progressValues)
            {
                if (!progress.FilesTotal.HasValue)
                {
                    return progressValues.All(static item => item.IsCompleted) ? 100 : 0;
                }

                totalFiles += progress.FilesTotal.Value;
                completedFiles += GetDisplayedRunProgressUnits(progress);
            }

            return totalFiles > 0
                ? Math.Clamp(completedFiles / totalFiles * 100, 0, 100)
                : progressValues.All(static item => item.IsCompleted) ? 100 : 0;
        }

        private bool TryCalculateAggregateRunTransferBytes(out long transferredBytes, out long totalBytes)
        {
            return TryCalculateAggregateRunTransferBytes(
                GetOrderedRunProgressSnapshots(),
                out transferredBytes,
                out totalBytes);
        }

        private bool TryCalculateAggregateRunTransferBytes(
            IReadOnlyList<DesktopRunProgressSnapshot> progressValues,
            out long transferredBytes,
            out long totalBytes)
        {
            transferredBytes = 0;
            totalBytes = 0;
            foreach (DesktopRunProgressSnapshot progress in progressValues)
            {
                if (!TryCalculateRunTransferBytes(progress, out long progressTransferredBytes, out long progressTotalBytes))
                {
                    continue;
                }

                transferredBytes += progressTransferredBytes;
                totalBytes += progressTotalBytes;
            }

            if (totalBytes <= 0)
            {
                transferredBytes = 0;
                return false;
            }

            transferredBytes = Math.Clamp(transferredBytes, 0, totalBytes);
            return true;
        }

        private bool TryCalculateRunTransferBytes(
            DesktopRunProgressSnapshot progress,
            out long transferredBytes,
            out long totalBytes)
        {
            transferredBytes = 0;
            totalBytes = 0;
            if (!IsCountedRunStage(progress.Stage) || progress.BytesTotal is not > 0)
            {
                return false;
            }

            totalBytes = progress.BytesTotal.Value;
            _runCompletedTransferBytesByPair.TryGetValue(progress.SyncPairId, out long observedCompletedBytes);
            transferredBytes = Math.Clamp(Math.Max(progress.BytesCompleted, observedCompletedBytes), 0, totalBytes);
            foreach (KeyValuePair<RunTransferProgressKey, long> activeTransfer in _runTransferBytesByKey)
            {
                if (activeTransfer.Key.SyncPairId != progress.SyncPairId)
                {
                    continue;
                }

                transferredBytes += Math.Max(0, activeTransfer.Value);
            }

            transferredBytes = Math.Clamp(transferredBytes, 0, totalBytes);
            return true;
        }
    }
}
