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
        private void StartSessionSyncInBackground(DesktopSyncApplicationHost host, string source)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    _syncCoreState = SyncCoreStateStarting;
                    await host.App.StartSyncAsync(CancellationToken.None).ConfigureAwait(false);
                    if (ReferenceEquals(_host, host))
                    {
                        _syncCoreState = SyncCoreStateRunning;
                    }
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning("Failed to start desktop sync after {0}: {1}", source, exception);
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    _syncCoreState = SyncCoreStateStartFailed;
                    ActivityReported?.Invoke(
                        this,
                        new DesktopActivitySnapshot(
                            "Error",
                            string.Empty,
                            DesktopActionRequiredMessageResolver.FromException(exception),
                            DateTime.UtcNow));
                }
            });
        }

        private void StartInitialSyncInBackground(DesktopSyncApplicationHost host, Guid syncPairId, string localPath)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    await host.App
                        .SyncNowAsync(
                            syncPairId,
                            SyncRunRequest.ForFull(SyncRunCause.InitialPopulation),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "Failed to request initial sync for newly added Cotton Sync folder {0}: {1}",
                        syncPairId,
                        exception);
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    ActivityReported?.Invoke(
                        this,
                        new DesktopActivitySnapshot(
                            "Error",
                            localPath,
                            DesktopActionRequiredMessageResolver.FromException(exception),
                            DateTime.UtcNow));
                }
            });
        }

        private async Task EnsureReleaseSecureTokenStorageAsync(CancellationToken cancellationToken)
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage = await _tokenStorageVerifier(cancellationToken)
                .ConfigureAwait(false);
            if (tokenStorage.IsReleaseSecure)
            {
                return;
            }

            throw new InvalidOperationException(CreateTokenStorageUnavailableMessage(tokenStorage));
        }

        private async Task<bool> CanUseStoredSessionAsync(CancellationToken cancellationToken)
        {
            DesktopTokenStorageCapabilitySnapshot tokenStorage;
            using CancellationTokenSource verificationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verificationCancellation.CancelAfter(_tokenStorageVerificationTimeout);
            try
            {
                tokenStorage = await _tokenStorageVerifier(verificationCancellation.Token)
                    .WaitAsync(verificationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning(
                    "Skipping desktop session restore because token storage verification timed out after {0} seconds.",
                    _tokenStorageVerificationTimeout.TotalSeconds);
                DesktopAuthDiagnosticsState.RecordSessionRestoreSkipped("skippedTokenStorageVerificationTimeout");
                return false;
            }

            if (tokenStorage.IsReleaseSecure)
            {
                return true;
            }

            Trace.TraceWarning(
                "Skipping desktop session restore because token storage is not release secure: {0}",
                tokenStorage.Details);
            DesktopAuthDiagnosticsState.RecordSessionRestoreSkipped("skippedTokenStorageUnavailable");
            return false;
        }

        private static string CreateTokenStorageUnavailableMessage(DesktopTokenStorageCapabilitySnapshot tokenStorage)
        {
            return "Secure token storage is unavailable: "
                + tokenStorage.Details
                + ". Configure Windows DPAPI or Linux Secret Service before signing in.";
        }
    }
}
