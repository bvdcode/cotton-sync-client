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
        private class FakeSyncPairWork : ISyncPairWork
        {
            private readonly Queue<Exception> _failures = [];

            public Exception? Failure { get; set; }

            public IReadOnlyList<Exception> Failures
            {
                set
                {
                    _failures.Clear();
                    foreach (Exception failure in value)
                    {
                        _failures.Enqueue(failure);
                    }
                }
            }

            public SyncPairSettings? LastSyncPair { get; private set; }

            public SyncRunRequest? LastRequest { get; private set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public int RunCount { get; private set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                LastSyncPair = syncPair;
                if (_failures.Count > 0)
                {
                    throw _failures.Dequeue();
                }

                if (Failure is not null)
                {
                    throw Failure;
                }

                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                Requests.Add(request);
                return RunOnceAsync(syncPair, cancellationToken);
            }
        }

        private class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private class ReleasingLockedFileSyncPairWork : ISyncPairWork
        {
            private readonly Action _releaseLock;
            private readonly LocalFileScanner _scanner = new();

            public ReleasingLockedFileSyncPairWork(Action releaseLock)
            {
                _releaseLock = releaseLock;
            }

            public int RunCount { get; private set; }

            public IReadOnlyList<string> ScannedPaths { get; private set; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                try
                {
                    IReadOnlyList<LocalFileSnapshot> files = await _scanner
                        .ScanAsync(syncPair.LocalRootPath, cancellationToken)
                        .ConfigureAwait(false);
                    ScannedPaths = files.Select(file => file.RelativePath).ToList();
                }
                catch (LocalFileUnavailableException) when (RunCount == 1)
                {
                    _releaseLock();
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
        }

        private class RestoringMissingRootSyncPairWork : ISyncPairWork
        {
            private readonly string _root;
            private readonly Action _restoreRoot;
            private readonly LocalFileScanner _scanner = new();

            public RestoringMissingRootSyncPairWork(string root, Action restoreRoot)
            {
                _root = root;
                _restoreRoot = restoreRoot;
            }

            public int RunCount { get; private set; }

            public IReadOnlyList<string> ScannedPaths { get; private set; } = [];

            public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCount++;
                try
                {
                    IReadOnlyList<LocalFileSnapshot> files = await _scanner
                        .ScanAsync(_root, cancellationToken)
                        .ConfigureAwait(false);
                    ScannedPaths = files.Select(file => file.RelativePath).ToList();
                }
                catch (DirectoryNotFoundException) when (RunCount == 1)
                {
                    _restoreRoot();
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
        }
    }
}
