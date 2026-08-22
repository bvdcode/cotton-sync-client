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
        private class BlockingSyncPairWork : ISyncPairWork
        {
            private readonly object _gate = new();
            private TaskCompletionSource _currentRunStarted = CreateCompletionSource();
            private TaskCompletionSource _currentRunRelease = CreateCompletionSource();
            private TaskCompletionSource _secondRunStarted = CreateCompletionSource();

            public int RunCount { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                await RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                TaskCompletionSource release;
                lock (_gate)
                {
                    RunCount++;
                    Requests.Add(request);
                    release = _currentRunRelease;
                    _currentRunStarted.TrySetResult();
                    if (RunCount >= 2)
                    {
                        _secondRunStarted.TrySetResult();
                    }
                }

                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            public void ReleaseCurrentRun()
            {
                lock (_gate)
                {
                    _currentRunRelease.TrySetResult();
                    _currentRunStarted = CreateCompletionSource();
                    _currentRunRelease = CreateCompletionSource();
                }
            }

            public Task WaitForRunAsync(TimeSpan timeout)
            {
                Task task;
                lock (_gate)
                {
                    task = _currentRunStarted.Task;
                }

                return task.WaitAsync(timeout);
            }

            public async Task WaitForRunCountAsync(int runCount, TimeSpan timeout)
            {
                if (RunCount >= runCount)
                {
                    return;
                }

                await _secondRunStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class BlockingFirstFailureSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _firstRunStarted = CreateCompletionSource();
            private readonly TaskCompletionSource _releaseFirstRun = CreateCompletionSource();

            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (Requests.Count != 1)
                {
                    return;
                }

                _firstRunStarted.TrySetResult();
                await _releaseFirstRun.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException("network down");
            }

            public void ReleaseFirstRun()
            {
                _releaseFirstRun.TrySetResult();
            }

            public Task WaitForFirstRunAsync(TimeSpan timeout)
            {
                return _firstRunStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class PreemptibleSyncPairWork : ISyncPairWork
        {
            private readonly TaskCompletionSource _firstRunStarted = CreateCompletionSource();

            public bool FirstRunCancellationObserved { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public async Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (Requests.Count != 1)
                {
                    return;
                }

                _firstRunStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    FirstRunCancellationObserved = true;
                    throw;
                }
            }

            public Task WaitForFirstRunAsync(TimeSpan timeout)
            {
                return _firstRunStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class TestIOException : IOException
        {
            public TestIOException(int hresult)
                : base("Synthetic I/O failure.")
            {
                HResult = hresult;
            }
        }
    }
}
