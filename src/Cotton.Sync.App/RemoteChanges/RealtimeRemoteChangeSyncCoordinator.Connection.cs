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
        private TaskCompletionSource<Exception?>? _connectionClosed;

        private void OnReconnected(object? sender, EventArgs args)
        {
            _logger.LogInformation("Server event connection restored; checking for missed changes.");
            QueueRemoteSync("ConnectionRestored");
        }

        private void OnClosed(object? sender, Exception? exception)
        {
            lock (_pendingGate)
            {
                _connectionClosed?.TrySetResult(exception);
            }
        }

        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
        {
            int attempts = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempts++;
                    TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_pendingGate)
                    {
                        _connectionClosed = closed;
                    }

                    try
                    {
                        using (CancellationTokenSource attempt =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            attempt.CancelAfter(_connectionAttemptTimeout);
                            await _authFlow.RestoreSessionAsync(attempt.Token).ConfigureAwait(false);
                            await _realtime.StartAsync(attempt.Token).ConfigureAwait(false);
                        }

                        _logger.LogInformation("Connected to server events after {Attempts} attempt(s).", attempts);
                        QueueRemoteSync("ConnectionEstablished");
                        attempts = 0;
                        Exception? closeException = await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            closeException,
                            "Server event connection closed; retrying after {RetryInterval}. Sync workers remain active.",
                            _connectionRetryInterval);
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
                    finally
                    {
                        lock (_pendingGate)
                        {
                            _connectionClosed = null;
                        }
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
