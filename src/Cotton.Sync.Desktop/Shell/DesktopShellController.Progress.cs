// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cotton;
using Cotton.Nodes;
using Cotton.Models;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using AppRunProgress = Cotton.Sync.App.Progress.AppRunProgress;
using AppTransferProgress = Cotton.Sync.App.Progress.AppTransferProgress;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController
    {
        private void OnTransferProgressChanged(AppTransferProgress progress)
        {
            DesktopTransferProgressSnapshot snapshot = ToTransferProgressSnapshot(progress);
            TransferProgressKey key = new(snapshot.SyncPairId, snapshot.Direction, snapshot.RelativePath);
            lock (_progressGate)
            {
                if (snapshot.IsCompleted)
                {
                    if (_currentTransfers.TryGetValue(key, out DesktopTransferProgressSnapshot? activeProgress)
                        && snapshot.OccurredAtUtc >= activeProgress.OccurredAtUtc)
                    {
                        _currentTransfers.Remove(key);
                    }
                }
                else
                {
                    if (!_currentTransfers.TryGetValue(key, out DesktopTransferProgressSnapshot? activeProgress)
                        || snapshot.OccurredAtUtc >= activeProgress.OccurredAtUtc)
                    {
                        _currentTransfers[key] = snapshot;
                    }
                }
            }

            TransferProgressChanged?.Invoke(this, snapshot);
        }

        private void OnRunProgressChanged(AppRunProgress progress)
        {
            DesktopRunProgressSnapshot snapshot = ToRunProgressSnapshot(progress);
            lock (_progressGate)
            {
                _aggregateRunProgress[snapshot.SyncPairId] = snapshot;
            }

            RunProgressChanged?.Invoke(this, snapshot);
        }

        private IReadOnlyList<DesktopTransferProgressSnapshot> GetCurrentTransfers()
        {
            lock (_progressGate)
            {
                return _currentTransfers.Values
                    .OrderBy(static progress => progress.SyncPairId)
                    .ToArray();
            }
        }

        private IReadOnlyList<DesktopRunProgressSnapshot> GetAggregateRunProgress()
        {
            lock (_progressGate)
            {
                return _aggregateRunProgress.Values
                    .OrderBy(static progress => progress.SyncPairId)
                    .ToArray();
            }
        }

        private void ClearProgressSnapshots()
        {
            lock (_progressGate)
            {
                _currentTransfers.Clear();
                _aggregateRunProgress.Clear();
            }
        }

        private static DesktopActivitySnapshot ToActivitySnapshot(AppSyncActivity activity)
        {
            return new DesktopActivitySnapshot(
                activity.Type.ToString(),
                activity.ItemPath ?? string.Empty,
                activity.Message,
                activity.OccurredAtUtc,
                activity.SyncPairId);
        }

        private static DesktopTransferProgressSnapshot ToTransferProgressSnapshot(AppTransferProgress progress)
        {
            return new DesktopTransferProgressSnapshot(
                progress.SyncPairId,
                progress.Direction,
                progress.RelativePath,
                progress.TransferredBytes,
                progress.TotalBytes,
                progress.IsCompleted,
                progress.OccurredAtUtc,
                progress.SpeedBytesPerSecond,
                progress.EstimatedTimeRemaining);
        }

        private static DesktopRunProgressSnapshot ToRunProgressSnapshot(AppRunProgress progress)
        {
            return new DesktopRunProgressSnapshot(
                progress.SyncPairId,
                progress.Stage,
                progress.FilesCompleted,
                progress.FilesTotal,
                progress.CurrentPath,
                progress.StartedAtUtc,
                progress.IsCompleted,
                progress.OccurredAtUtc,
                progress.BytesCompleted,
                progress.BytesTotal,
                progress.Causes,
                progress.IsFull,
                progress.RequestedPathCount);
        }
    }
}
