// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.ViewModels
{
    internal class StoredSessionRestoreRetryCoordinator
    {
        private readonly Func<string, CancellationToken, Task<DesktopStoredSessionRestoreSnapshot>> _restoreAsync;
        private readonly IDesktopUiDispatcher _uiDispatcher;
        private readonly TimeSpan _retryInterval;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly Action<DesktopStoredSessionRestoreSnapshot, string> _applyResult;
        private CancellationTokenSource? _retryCancellation;
        private Task? _retryTask;

        public StoredSessionRestoreRetryCoordinator(
            Func<string, CancellationToken, Task<DesktopStoredSessionRestoreSnapshot>> restoreAsync,
            IDesktopUiDispatcher uiDispatcher,
            TimeSpan retryInterval,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            Action<DesktopStoredSessionRestoreSnapshot, string> applyResult)
        {
            _restoreAsync = restoreAsync ?? throw new ArgumentNullException(nameof(restoreAsync));
            _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryInterval, TimeSpan.Zero);
            _retryInterval = retryInterval;
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
            _applyResult = applyResult ?? throw new ArgumentNullException(nameof(applyResult));
        }

        public Task? RetryTask => _retryTask;

        public void Begin(string serverUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
            Cancel();
            CancellationTokenSource cancellation = new();
            _retryCancellation = cancellation;
            _retryTask = RetryUntilResolvedAsync(serverUrl, cancellation);
        }

        public Task Cancel()
        {
            CancellationTokenSource? cancellation = _retryCancellation;
            Task retryTask = _retryTask ?? Task.CompletedTask;
            _retryCancellation = null;
            _retryTask = null;
            cancellation?.Cancel();
            return retryTask;
        }

        public static async Task IgnoreCancellationAsync(Task retryTask)
        {
            try
            {
                await retryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RetryUntilResolvedAsync(string serverUrl, CancellationTokenSource retryCancellation)
        {
            CancellationToken cancellationToken = retryCancellation.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _delayAsync(_retryInterval, cancellationToken).ConfigureAwait(false);
                    DesktopStoredSessionRestoreSnapshot result = await _restoreAsync(serverUrl, cancellationToken)
                        .ConfigureAwait(false);
                    bool shouldContinue = result.Session is null && result.HasStoredSession;
                    await _uiDispatcher.InvokeAsync(
                        () => _applyResult(result, "Session restored automatically"),
                        cancellationToken).ConfigureAwait(false);
                    if (!shouldContinue)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Automatic stored session restore failed: {0}", exception);
            }
            finally
            {
                if (ReferenceEquals(_retryCancellation, retryCancellation))
                {
                    _retryCancellation = null;
                    _retryTask = null;
                }

                retryCancellation.Dispose();
            }
        }
    }
}
