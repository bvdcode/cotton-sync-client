// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Composition;

namespace Cotton.Sync.Desktop.Shell
{
    internal partial class DesktopShellController
    {
        private async Task EnsureSyncStartedAsync(DesktopSyncApplicationHost host, CancellationToken cancellationToken)
        {
            await _syncStartupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_host, host))
                {
                    throw new OperationCanceledException("The active session changed.", cancellationToken);
                }

                if (_syncCoreState == SyncCoreStateRunning)
                {
                    return;
                }

                _syncCoreFailureMessage = null;
                _syncCoreState = SyncCoreStateStarting;
                try
                {
                    await host.App.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                    if (ReferenceEquals(_host, host))
                    {
                        _syncCoreState = SyncCoreStateRunning;
                        OnStatusChanged(host.StatusPublisher.Current);
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError("Sync startup failed: {0}", exception);
                    if (ReferenceEquals(_host, host))
                    {
                        _syncCoreFailureMessage = "Sync could not start. "
                            + DesktopActionRequiredMessageResolver.FromException(exception)
                            + " Use Sync now to retry.";
                        _syncCoreState = SyncCoreStateStartFailed;
                        OnStatusChanged(host.StatusPublisher.Current);
                    }

                    throw;
                }
            }
            finally
            {
                _syncStartupGate.Release();
            }
        }

        private DesktopSyncPairStatusSnapshot ApplySyncStartupFailure(DesktopSyncPairStatusSnapshot status)
        {
            if (_syncCoreFailureMessage is null || status.Status == "Disabled")
            {
                return status;
            }

            return status with
            {
                Status = "Error",
                LastError = _syncCoreFailureMessage,
                CurrentOperation = _syncCoreFailureMessage,
            };
        }
    }
}
