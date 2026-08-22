// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using Cotton.Sdk;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Desktop.Startup;
using Cotton.Sync.Desktop.Updates;
using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop.Tests.ViewModels
{
    public partial class ShellViewModelSyncPairCommandTests
    {
        private partial class FakeDesktopShellController : IDesktopShellController
        {

            public void ReportActivity(DesktopActivitySnapshot activity)
            {
                ActivityReported?.Invoke(this, activity);
            }


            public void ReportSessionRevoked(DesktopSessionRevocationSnapshot sessionRevocation)
            {
                SessionRevoked?.Invoke(this, sessionRevocation);
            }


            public void ReportStatus(DesktopSyncStatusSnapshot status)
            {
                StatusChanged?.Invoke(this, status);
            }


            public void ReportTransferProgress(DesktopTransferProgressSnapshot progress)
            {
                TransferProgressChanged?.Invoke(this, progress);
            }


            public void ReportRunProgress(DesktopRunProgressSnapshot progress)
            {
                RunProgressChanged?.Invoke(this, progress);
            }


            public async Task<DesktopShellSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LoadStarted = true;
                if (LoadException is not null)
                {
                    throw LoadException;
                }

                if (LoadCompletion is not null)
                {
                    await LoadCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return _snapshot;
            }


            public Task SetSyncPairEnabledAsync(
                Guid syncPairId,
                bool enabled,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnabledSyncPairId = syncPairId;
                EnabledSyncPairValue = enabled;
                return Task.CompletedTask;
            }


            public async Task RemoveSyncPairAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RemoveSyncPairThreadId = Environment.CurrentManagedThreadId;
                RemovedSyncPairId = syncPairId;
                RemoveSyncPairStarted?.TrySetResult();
                if (RemoveSyncPairCompletion is not null)
                {
                    await RemoveSyncPairCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }


            public Task RenameSyncPairAsync(
                Guid syncPairId,
                string displayName,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RenamedSyncPairId = syncPairId;
                RenamedSyncPairDisplayName = displayName;
                return Task.CompletedTask;
            }


            public async Task<DesktopServerProbeResult> ProbeServerAsync(
                string serverUrl,
                CancellationToken cancellationToken = default)
            {
                if (!IgnoreServerProbeCancellation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ProbedServerUrls.Add(serverUrl);
                if (ServerProbeExceptionsByUrl.TryGetValue(serverUrl, out Queue<Exception>? exceptions)
                    && exceptions.Count > 0)
                {
                    throw exceptions.Dequeue();
                }

                if (ServerProbeCompletionsByUrl.TryGetValue(serverUrl, out TaskCompletionSource<DesktopServerProbeResult>? completion))
                {
                    return IgnoreServerProbeCancellation
                        ? await completion.Task.ConfigureAwait(false)
                        : await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (ServerProbeResultsByUrl.TryGetValue(serverUrl, out DesktopServerProbeResult? result))
                {
                    return result;
                }

                return ServerProbeResult ?? throw new NotSupportedException();
            }


            public Task<DesktopStoredSessionRestoreSnapshot> RestoreStoredSessionAsync(
                string serverUrl,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestoreStoredSessionCalls++;
                RestoredSessionServerUrl = serverUrl;
                return StoredSessionRestoreCompletion is null
                    ? Task.FromResult(StoredSessionRestoreSnapshot)
                    : StoredSessionRestoreCompletion.Task.WaitAsync(cancellationToken);
            }


            public Task<AuthSession> SignInAsync(
                DesktopSignInRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SignInRequest = request;
                if (SignInException is not null)
                {
                    throw SignInException;
                }

                return Task.FromResult(new AuthSession(
                    Guid.NewGuid(),
                    request.Username,
                    request.Username,
                    false));
            }


            public Task<AuthSession> SignInWithBrowserAsync(
                string serverUrl,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrowserSignInServerUrl = serverUrl;
                if (SignInException is not null)
                {
                    throw SignInException;
                }

                AuthSession session = new(
                    Guid.NewGuid(),
                    "browser",
                    "browser@example.test",
                    false);
                return BrowserSignInCompletion is null
                    ? Task.FromResult(session)
                    : WaitForBrowserSignInAsync(BrowserSignInCompletion, cancellationToken);
            }


            private static async Task<AuthSession> WaitForBrowserSignInAsync(
                TaskCompletionSource<AuthSession> completion,
                CancellationToken cancellationToken)
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static state =>
                    {
                        TaskCompletionSource<AuthSession> taskCompletion = (TaskCompletionSource<AuthSession>)state!;
                        taskCompletion.TrySetCanceled();
                    },
                    completion);
                return await completion.Task.ConfigureAwait(false);
            }
        }
    }
}
