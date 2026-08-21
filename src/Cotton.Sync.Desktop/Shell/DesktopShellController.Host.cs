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
        private DesktopSyncApplicationHost RequireHost()
        {
            return _host ?? throw new InvalidOperationException("Sign in before running sync commands.");
        }

        private DesktopSyncApplicationHost? DetachHost()
        {
            DesktopSyncApplicationHost? host = _host;
            _host = null;
            _activeSession = null;
            _syncCoreState = SyncCoreStateSignedOut;
            _activitySubscription?.Dispose();
            _activitySubscription = null;
            _sessionRevocationSubscription?.Dispose();
            _sessionRevocationSubscription = null;
            _statusSubscription?.Dispose();
            _statusSubscription = null;
            _transferProgressSubscription?.Dispose();
            _transferProgressSubscription = null;
            _runProgressSubscription?.Dispose();
            _runProgressSubscription = null;
            ClearProgressSnapshots();
            return host;
        }

        private async Task ReplaceHostAsync(
            DesktopSyncApplicationHost host,
            AuthSession session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesktopSyncApplicationHost? previous = _host;
            _host = host;
            _activeSession = session;
            _syncCoreState = SyncCoreStateStopped;
            _activitySubscription?.Dispose();
            _sessionRevocationSubscription?.Dispose();
            _statusSubscription?.Dispose();
            _transferProgressSubscription?.Dispose();
            _runProgressSubscription?.Dispose();
            ClearProgressSnapshots();
            _statusSubscription = host.StatusPublisher.Subscribe(new DesktopShellObserver<SyncAppStatus>(OnStatusChanged));
            _activitySubscription = host.ActivityPublisher.Subscribe(new DesktopShellObserver<AppSyncActivity>(OnActivityReported));
            _sessionRevocationSubscription = host.SessionRevocationPublisher.Subscribe(new DesktopShellObserver<SessionRevocationEvent>(OnSessionRevoked));
            _transferProgressSubscription = host.TransferProgressPublisher.Subscribe(new DesktopShellObserver<AppTransferProgress>(OnTransferProgressChanged));
            _runProgressSubscription = host.RunProgressPublisher.Subscribe(new DesktopShellObserver<AppRunProgress>(OnRunProgressChanged));
            if (previous is not null)
            {
                await StopAndDisposeHostAsync(previous).ConfigureAwait(false);
            }
        }

        private static async Task StopAndDisposeHostAsync(DesktopSyncApplicationHost host)
        {
            try
            {
                await host.App.StopSyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Failed to stop previous desktop sync host: {0}", exception);
            }
            finally
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }

        private DesktopSyncStatusSnapshot ToStatusSnapshot(SyncAppStatus status)
        {
            IReadOnlyDictionary<Guid, (bool IsEnabled, string LocalRootPath)> knownSyncPairSettings =
                GetKnownSyncPairSettingsSnapshot();
            return new DesktopSyncStatusSnapshot(
                status.SyncPairs
                    .Select(syncPair => ToStatusSnapshot(syncPair, knownSyncPairSettings))
                    .ToList());
        }

        private static DesktopSyncPairStatusSnapshot ToStatusSnapshot(
            SyncPairStatus syncPair,
            IReadOnlyDictionary<Guid, (bool IsEnabled, string LocalRootPath)> knownSyncPairSettings)
        {
            if (knownSyncPairSettings.TryGetValue(
                    syncPair.SyncPairId,
                    out (bool IsEnabled, string LocalRootPath) settings))
            {
                string? localRootError = GetLocalRootUnavailableError(settings.IsEnabled, settings.LocalRootPath);
                if (localRootError is not null)
                {
                    return new DesktopSyncPairStatusSnapshot(
                        syncPair.SyncPairId,
                        "Error",
                        localRootError,
                        "Action required: " + localRootError,
                        syncPair.LastSuccessfulSyncAtUtc);
                }
            }

            return new DesktopSyncPairStatusSnapshot(
                syncPair.SyncPairId,
                ToStatusText(syncPair),
                syncPair.LastError,
                syncPair.CurrentOperation,
                syncPair.LastSuccessfulSyncAtUtc);
        }

        private static string ToStatusText(SyncPairStatus status)
        {
            return status.State switch
            {
                SyncPairRunState.Disabled => "Disabled",
                SyncPairRunState.Idle => "Idle",
                SyncPairRunState.Scanning => "Scanning",
                SyncPairRunState.Syncing => "Syncing",
                SyncPairRunState.Waiting => "Waiting",
                SyncPairRunState.Paused => "Paused",
                SyncPairRunState.Offline => "Offline",
                SyncPairRunState.Conflict => "Conflict",
                SyncPairRunState.Error => "Error",
                _ => status.State.ToString(),
            };
        }

        private void OnStatusChanged(SyncAppStatus status)
        {
            StatusChanged?.Invoke(this, ToStatusSnapshot(status));
        }

        private void OnActivityReported(AppSyncActivity activity)
        {
            ActivityReported?.Invoke(this, ToActivitySnapshot(activity));
        }

        private void OnSessionRevoked(SessionRevocationEvent sessionRevocation)
        {
            _activeSession = null;
            DesktopAuthDiagnosticsState.RecordSessionRevoked(sessionRevocation.OccurredAtUtc);
            SessionRevoked?.Invoke(this, new DesktopSessionRevocationSnapshot(sessionRevocation.OccurredAtUtc));
        }
    }
}
