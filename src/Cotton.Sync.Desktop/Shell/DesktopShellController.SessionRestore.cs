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
        public Task<DesktopStoredSessionRestoreSnapshot> RestoreStoredSessionAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            Uri parsedServerUrl = ParseServerUrl(serverUrl);
            return TryRestoreSessionAsync(parsedServerUrl, cancellationToken);
        }

        private async Task<DesktopStoredSessionRestoreSnapshot> TryRestoreSessionAsync(
            Uri serverUrl,
            CancellationToken cancellationToken)
        {
            DesktopStoredSessionRestoreSnapshot? activeSession = TryGetActiveSession(serverUrl);
            if (activeSession is not null)
            {
                return activeSession;
            }

            if (!await CanUseStoredSessionAsync(cancellationToken).ConfigureAwait(false))
            {
                return new DesktopStoredSessionRestoreSnapshot(null, false, null);
            }

            DesktopSyncApplicationHost host = _factory.Create(serverUrl);
            return await RestoreStoredSessionAsync(host, serverUrl, cancellationToken).ConfigureAwait(false);
        }

        private DesktopStoredSessionRestoreSnapshot? TryGetActiveSession(Uri serverUrl)
        {
            DesktopSyncApplicationHost? activeHost = _host;
            AuthSession? activeSession = _activeSession;
            if (activeHost is null || activeSession is null || !activeHost.ServerUrl.Equals(serverUrl))
            {
                return null;
            }

            return new DesktopStoredSessionRestoreSnapshot(activeSession, true, null);
        }

        private async Task<DesktopStoredSessionRestoreSnapshot> RestoreStoredSessionAsync(
            DesktopSyncApplicationHost host,
            Uri serverUrl,
            CancellationToken cancellationToken)
        {
            bool hasStoredSession = false;
            try
            {
                if (await host.TokenStore.GetAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    DesktopAuthDiagnosticsState.RecordSessionRestoreSkipped("skippedNoStoredTokens");
                    await host.DisposeAsync().ConfigureAwait(false);
                    return new DesktopStoredSessionRestoreSnapshot(null, false, null);
                }

                hasStoredSession = true;
                using CancellationTokenSource restoreCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                restoreCancellation.CancelAfter(_savedSessionRestoreTimeout);
                RestoredSession restoredSession = await RestoreSessionWithRetryAsync(
                        host,
                        serverUrl,
                        restoreCancellation.Token)
                    .ConfigureAwait(false);
                DesktopAuthDiagnosticsState.RecordSessionRestoreSucceeded(restoredSession.Attempts);
                await ReplaceHostAsync(host, restoredSession.Session, cancellationToken).ConfigureAwait(false);
                StartSessionSyncInBackground(host, "session restore");
                return new DesktopStoredSessionRestoreSnapshot(restoredSession.Session, true, null);
            }
            catch (Cotton.Sdk.CottonApiException exception) when (IsAuthSessionRejected(exception))
            {
                Trace.TraceWarning("Failed to restore desktop session: {0}", exception);
                DesktopAuthDiagnosticsState.RecordSessionRestoreRejected(attempts: 1, exception);
                await host.TokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
                return new DesktopStoredSessionRestoreSnapshot(
                    null,
                    false,
                    "Saved session expired. Sign in again to continue syncing.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning(
                    "Timed out restoring desktop session for {0} after {1} seconds.",
                    serverUrl,
                    _savedSessionRestoreTimeout.TotalSeconds);
                DesktopAuthDiagnosticsState.RecordSessionRestoreFailed(
                    "timedOut",
                    attempts: 1,
                    new TimeoutException("Saved session restore timed out."));
                await host.DisposeAsync().ConfigureAwait(false);
                return new DesktopStoredSessionRestoreSnapshot(
                    null,
                    hasStoredSession,
                    "Saved session could not be restored. Cotton Sync will retry automatically.");
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Failed to restore desktop session for {0}: {1}", serverUrl, exception);
                DesktopAuthDiagnosticsState.RecordSessionRestoreFailed(
                    "failed",
                    attempts: 1,
                    exception);
                await host.DisposeAsync().ConfigureAwait(false);
                return new DesktopStoredSessionRestoreSnapshot(
                    null,
                    hasStoredSession,
                    DesktopActionRequiredMessageResolver.FromException(exception));
            }
        }

        private async Task<RestoredSession> RestoreSessionWithRetryAsync(
            DesktopSyncApplicationHost host,
            Uri serverUrl,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= SavedSessionRestoreMaxAttempts; attempt++)
            {
                try
                {
                    AuthSession session = await host.App.RestoreSessionAsync(cancellationToken)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return new RestoredSession(session, attempt);
                }
                catch (Exception exception) when (IsTransientSessionRestoreFailure(exception, cancellationToken))
                {
                    if (attempt == SavedSessionRestoreMaxAttempts)
                    {
                        Trace.TraceWarning(
                            "Failed to restore desktop session because the server is unreachable after {0} attempts: {1}",
                            SavedSessionRestoreMaxAttempts,
                            serverUrl);
                        DesktopAuthDiagnosticsState.RecordSessionRestoreFailed(
                            "transientFailure",
                            attempt,
                            exception);
                        throw;
                    }

                    TimeSpan delay = TimeSpan.FromTicks(_savedSessionRestoreRetryBaseDelay.Ticks * attempt);
                    Trace.TraceWarning(
                        "Desktop session restore attempt {0} failed transiently for {1}. Retrying after {2} seconds.",
                        attempt,
                        serverUrl,
                        delay.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("Desktop session restore retry loop exited unexpectedly.");
        }

        private static bool IsTransientSessionRestoreFailure(Exception exception, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return exception switch
            {
                Cotton.Sdk.CottonApiException apiException => IsTransientSessionRestoreStatus(apiException.StatusCode),
                HttpRequestException requestException => IsTransientSessionRestoreStatus(requestException.StatusCode),
                IOException => true,
                TimeoutException => true,
                TaskCanceledException => true,
                _ => false,
            };
        }

        private static bool IsTransientSessionRestoreStatus(HttpStatusCode? statusCode)
        {
            return statusCode is null
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }

        private static bool IsAuthSessionRejected(Cotton.Sdk.CottonApiException exception)
        {
            return exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        }
    }
}
