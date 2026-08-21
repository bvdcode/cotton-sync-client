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
        private (bool HasByteRate, bool HasByteEstimate) AddRunByteRateDetails(List<string> parts)
        {
            bool hasAggregateTransferBytes = TryCalculateAggregateRunTransferBytes(
                out long transferredBytes,
                out long totalBytes);
            if (hasAggregateTransferBytes
                && TryAddAggregateRunTransferRate(
                    parts,
                    transferredBytes,
                    totalBytes,
                    out bool hasByteEstimate))
            {
                return (true, hasByteEstimate);
            }

            if (!hasAggregateTransferBytes && TryAddActiveTransferRate(parts))
            {
                return (true, false);
            }

            if (TryAddRecordedRunTransferRate(parts))
            {
                return (true, false);
            }

            return (false, false);
        }

        private bool TryAddAggregateRunTransferRate(
            List<string> parts,
            long transferredBytes,
            long totalBytes,
            out bool hasByteEstimate)
        {
            hasByteEstimate = false;
            if (!TryGetRunTransferSpeed(out double bytesPerSecond))
            {
                return false;
            }

            parts.Add(FormatBytes(bytesPerSecond) + "/s");
            if (totalBytes <= transferredBytes)
            {
                return true;
            }

            TimeSpan estimatedTimeRemaining = _runTransferEstimatedTimeRemaining
                ?? TimeSpan.FromSeconds((totalBytes - transferredBytes) / bytesPerSecond);
            parts.Add(FormatDuration(estimatedTimeRemaining) + " left");
            hasByteEstimate = true;
            return true;
        }

        private bool TryAddActiveTransferRate(List<string> parts)
        {
            if (!HasActiveTransferProgress)
            {
                return false;
            }

            string activeTransferRate = CreateAggregateTransferMetricDetails(
                _transferProgressByKey.Values,
                includeEstimatedTimeRemaining: false).Rate;
            if (string.IsNullOrWhiteSpace(activeTransferRate))
            {
                return false;
            }

            parts.Add(activeTransferRate);
            return true;
        }

        private bool TryAddRecordedRunTransferRate(List<string> parts)
        {
            if (_runTransferSpeedBytesPerSecond is not > 0)
            {
                return false;
            }

            parts.Add(FormatBytes(_runTransferSpeedBytesPerSecond.Value) + "/s");
            return true;
        }

        private string FormatCurrentRunProgressRate(double unitsPerSecond)
        {
            List<DesktopRunProgressSnapshot> progressValues = GetOrderedRunProgressSnapshots();
            if (progressValues.Count > 0
                && progressValues.All(static progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders))
            {
                return FormatCloudItemRate(unitsPerSecond);
            }

            if (progressValues.Count > 0
                && progressValues.All(static progress => progress.Stage == SyncRunProgressStage.FinalizingCloudFiles))
            {
                return FormatFolderRate(unitsPerSecond);
            }

            return FormatFileRate(unitsPerSecond);
        }

        private void ClearRunTransferMetrics()
        {
            _runCompletedTransferBytesByPair.Clear();
            _runCompletedTransferBytesByKey.Clear();
            _runTransferBytesByKey.Clear();
            _runFileProgressSamples.Clear();
            _runTransferSamples.Clear();
            _runTransferredBytes = 0;
            _runTransferSpeedBytesPerSecond = null;
            _runTransferEstimatedTimeRemaining = null;
            _lastRunTransferSpeedOccurredAtUtc = null;
            _lastRunTransferEstimateOccurredAtUtc = null;
            _currentRunProgressFilesPerSecond = null;
            _currentRunProgressEstimatedTimeRemaining = null;
            _lastRunProgressFileRateOccurredAtUtc = null;
            _lastRunProgressEstimateOccurredAtUtc = null;
        }

        private void TrackRunTransferProgress(DesktopTransferProgressSnapshot progress)
        {
            if (!IsRunTransferDirection(progress.Direction))
            {
                return;
            }

            long effectiveTransferredBytes = Math.Max(0, progress.TransferredBytes);
            if (progress.TotalBytes.HasValue)
            {
                effectiveTransferredBytes = Math.Min(effectiveTransferredBytes, progress.TotalBytes.Value);
            }

            RunTransferProgressKey key = CreateTransferProgressKey(progress);
            _runTransferBytesByKey.TryGetValue(key, out long previousTransferredBytes);
            if (effectiveTransferredBytes < previousTransferredBytes)
            {
                previousTransferredBytes = 0;
            }

            if (progress.IsCompleted)
            {
                _runTransferBytesByKey.Remove(key);
                TrackCompletedRunTransferBytes(key, effectiveTransferredBytes);
            }
            else
            {
                _runTransferBytesByKey[key] = effectiveTransferredBytes;
            }

            long transferredDelta = effectiveTransferredBytes - previousTransferredBytes;
            if (transferredDelta <= 0)
            {
                return;
            }

            _runTransferredBytes += transferredDelta;
            AddRunTransferSample(_runTransferredBytes, progress.OccurredAtUtc);
        }

        private static RunTransferProgressKey CreateTransferProgressKey(
            DesktopTransferProgressSnapshot progress)
        {
            return new RunTransferProgressKey(progress.SyncPairId, progress.Direction, progress.RelativePath);
        }

        private void TrackCompletedRunTransferBytes(RunTransferProgressKey key, long completedBytes)
        {
            if (completedBytes <= 0)
            {
                return;
            }

            _runCompletedTransferBytesByKey.TryGetValue(key, out long existingCompletedBytes);
            if (completedBytes > existingCompletedBytes)
            {
                _runCompletedTransferBytesByKey[key] = completedBytes;
                long completedBytesDelta = completedBytes - existingCompletedBytes;
                _runCompletedTransferBytesByPair.TryGetValue(key.SyncPairId, out long pairCompletedBytes);
                _runCompletedTransferBytesByPair[key.SyncPairId] = pairCompletedBytes + completedBytesDelta;
            }
        }

        private void AddRunTransferSample(long transferredBytes, DateTime occurredAtUtc)
        {
            if (_runTransferSamples.Count == 0 && transferredBytes <= 0)
            {
                return;
            }

            if (_runTransferSamples.Count > 0
                && occurredAtUtc - _runTransferSamples.Last().OccurredAtUtc > RunTransferMetricsWindow)
            {
                _runTransferSamples.Clear();
                _runTransferSpeedBytesPerSecond = null;
                _lastRunTransferSpeedOccurredAtUtc = null;
            }

            if (_runTransferSamples.Count == 0)
            {
                _runTransferSamples.Enqueue(new RunTransferProgressSample(transferredBytes, occurredAtUtc));
                return;
            }

            RunTransferProgressSample lastSample = _runTransferSamples.Last();
            if (occurredAtUtc == lastSample.OccurredAtUtc)
            {
                if (transferredBytes > lastSample.TransferredBytes)
                {
                    ReplaceLastRunTransferSample(new RunTransferProgressSample(transferredBytes, occurredAtUtc));
                    UpdateRunTransferSpeedFromSamples();
                }

                return;
            }

            if (occurredAtUtc < lastSample.OccurredAtUtc)
            {
                return;
            }

            if (transferredBytes < _runTransferSamples.Last().TransferredBytes)
            {
                _runTransferSamples.Clear();
                _runTransferSpeedBytesPerSecond = null;
                _lastRunTransferSpeedOccurredAtUtc = null;
                _runTransferSamples.Enqueue(new RunTransferProgressSample(transferredBytes, occurredAtUtc));
                return;
            }

            _runTransferSamples.Enqueue(new RunTransferProgressSample(transferredBytes, occurredAtUtc));
            PruneRunTransferSamples(occurredAtUtc);
            UpdateRunTransferSpeedFromSamples();
        }

        private void ReplaceLastRunTransferSample(RunTransferProgressSample sample)
        {
            RunTransferProgressSample[] samples = _runTransferSamples.ToArray();
            _runTransferSamples.Clear();
            for (int index = 0; index < samples.Length - 1; index++)
            {
                _runTransferSamples.Enqueue(samples[index]);
            }

            _runTransferSamples.Enqueue(sample);
        }

        private void PruneRunTransferSamples(DateTime occurredAtUtc)
        {
            while (_runTransferSamples.Count > 2
                && occurredAtUtc - _runTransferSamples.Peek().OccurredAtUtc > RunTransferMetricsWindow)
            {
                _runTransferSamples.Dequeue();
            }
        }

        private void UpdateRunTransferSpeedFromSamples()
        {
            if (_runTransferSamples.Count < 2)
            {
                return;
            }

            RunTransferProgressSample firstSample = _runTransferSamples.Peek();
            RunTransferProgressSample lastSample = _runTransferSamples.Last();
            TimeSpan elapsed = lastSample.OccurredAtUtc - firstSample.OccurredAtUtc;
            long transferredBytes = lastSample.TransferredBytes - firstSample.TransferredBytes;
            if (elapsed < MinimumRunTransferSampleDuration || transferredBytes <= 0)
            {
                return;
            }

            double observedBytesPerSecond = transferredBytes / elapsed.TotalSeconds;
            UpdateRunTransferSpeed(observedBytesPerSecond, lastSample.OccurredAtUtc);
        }

        private void UpdateRunTransferSpeed(double observedBytesPerSecond, DateTime occurredAtUtc)
        {
            if (!_runTransferSpeedBytesPerSecond.HasValue
                || !_lastRunTransferSpeedOccurredAtUtc.HasValue
                || occurredAtUtc <= _lastRunTransferSpeedOccurredAtUtc.Value)
            {
                _runTransferSpeedBytesPerSecond = observedBytesPerSecond;
                _lastRunTransferSpeedOccurredAtUtc = occurredAtUtc;
                return;
            }

            TimeSpan sampleElapsed = occurredAtUtc - _lastRunTransferSpeedOccurredAtUtc.Value;
            double smoothingFactor = CalculateExponentialSmoothingFactor(sampleElapsed, RunProgressEstimateSmoothingPeriod);
            _runTransferSpeedBytesPerSecond = Math.Max(
                0,
                _runTransferSpeedBytesPerSecond.Value
                    + ((observedBytesPerSecond - _runTransferSpeedBytesPerSecond.Value) * smoothingFactor));
            _lastRunTransferSpeedOccurredAtUtc = occurredAtUtc;
        }

        private void UpdateRunProgressEstimatedTimeRemaining(IReadOnlyList<DesktopRunProgressSnapshot> progressValues)
        {
            if (!TryCalculateAggregateRunProgressEstimate(
                progressValues,
                out double observedFilesPerSecond,
                out double remainingFiles,
                out DateTime occurredAtUtc))
            {
                _currentRunProgressFilesPerSecond = null;
                _currentRunProgressEstimatedTimeRemaining = null;
                _lastRunProgressFileRateOccurredAtUtc = null;
                _lastRunProgressEstimateOccurredAtUtc = null;
                return;
            }

            UpdateRunFileRate(observedFilesPerSecond, occurredAtUtc);
            if (progressValues.Any(static progress => progress.Stage == SyncRunProgressStage.CreatingPlaceholders))
            {
                _currentRunProgressEstimatedTimeRemaining = null;
                _lastRunProgressEstimateOccurredAtUtc = null;
                return;
            }

            TimeSpan? rawEstimatedTimeRemaining = _currentRunProgressFilesPerSecond is > 0
                ? TimeSpan.FromSeconds(remainingFiles / _currentRunProgressFilesPerSecond.Value)
                : null;
            _currentRunProgressEstimatedTimeRemaining = rawEstimatedTimeRemaining.HasValue
                ? SmoothEstimatedTimeRemaining(
                    rawEstimatedTimeRemaining.Value,
                    occurredAtUtc,
                    _currentRunProgressEstimatedTimeRemaining,
                    _lastRunProgressEstimateOccurredAtUtc)
                : null;
            _lastRunProgressEstimateOccurredAtUtc = rawEstimatedTimeRemaining.HasValue ? occurredAtUtc : null;
        }
    }
}
