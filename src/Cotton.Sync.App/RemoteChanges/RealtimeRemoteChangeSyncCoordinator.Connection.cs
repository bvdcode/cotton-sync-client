// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.RemoteChanges
{
    public partial class RealtimeRemoteChangeSyncCoordinator
    {
        private readonly IAuthFlow _authFlow;
        private readonly TimeSpan _connectionRetryInterval;
        private readonly TimeSpan _connectionAttemptTimeout;
        private Task? _connectionTask;

        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
        {
            int attempts = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempts++;
                    try
                    {
                        using CancellationTokenSource attempt =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        attempt.CancelAfter(_connectionAttemptTimeout);
                        await _authFlow.RestoreSessionAsync(attempt.Token).ConfigureAwait(false);
                        await _realtime.StartAsync(attempt.Token).ConfigureAwait(false);
                        _logger.LogInformation("Connected to server events after {Attempts} attempt(s).", attempts);
                        QueueRemoteSync("ConnectionEstablished");
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(
                            exception,
                            "Server event connection failed; retrying after {RetryInterval}. Sync workers remain active.",
                            _connectionRetryInterval);
                    }

                    await Task.Delay(_connectionRetryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
