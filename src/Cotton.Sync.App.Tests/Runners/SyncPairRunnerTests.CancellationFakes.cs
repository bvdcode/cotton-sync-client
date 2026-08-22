// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sdk;
using Cotton.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class SyncPairRunnerTests
    {
        private class CancellationObservingSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _cancellationObserved = CreateCompletionSource();
            private readonly TaskCompletionSource _runStarted = CreateCompletionSource();

            public int RunCount { get; private set; }

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                _runStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, cancellationToken);
            }

            public async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
            {
                try
                {
                    await _cancellationObserved.Task.WaitAsync(timeout).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                return _runStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class CancellationSideEffectSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _cancellationObserved = CreateCompletionSource();
            private readonly TaskCompletionSource _runStarted = CreateCompletionSource();
            private readonly Exception _sideEffect;

            public CancellationSideEffectSyncPairWork(Exception sideEffect)
            {
                _sideEffect = sideEffect;
            }

            public int RunCount { get; private set; }

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                _runStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw _sideEffect;
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, cancellationToken);
            }

            public async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
            {
                try
                {
                    await _cancellationObserved.Task.WaitAsync(timeout).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                return _runStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class BlockingFirstRunSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _runStarted = CreateCompletionSource();
            private readonly TaskCompletionSource _releaseRun = CreateCompletionSource();

            public int RunCount { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                _runStarted.TrySetResult();
                if (RunCount == 1)
                {
                    await _releaseRun.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return RunOnceAsync(syncPair, cancellationToken);
            }

            public void ReleaseRun()
            {
                _releaseRun.TrySetResult();
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                return _runStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
