// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.Tests.LocalChanges
{
    public partial class LocalChangeSyncCoordinatorTests
    {
        private class FakeSyncPairSettingsStore : ISyncPairSettingsStore
        {
            private readonly IReadOnlyList<SyncPairSettings> _syncPairs;

            public FakeSyncPairSettingsStore(IReadOnlyList<SyncPairSettings> syncPairs)
            {
                _syncPairs = syncPairs;
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_syncPairs.SingleOrDefault(pair => pair.Id == syncPairId));
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_syncPairs);
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class FakeSyncSupervisor : ISyncSupervisor
        {
            private readonly TaskCompletionSource _syncRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseSyncNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public bool BlockSyncNow { get; set; }

            public List<SyncRunRequest> Requests { get; } = [];

            public Queue<Exception> SyncNowExceptions { get; } = [];

            public int SyncNowCallCount { get; private set; }

            public Guid? LastSyncNowPairId { get; private set; }

            public SyncRunRequest? LastRequest { get; private set; }

            public Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(bool startPaused, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return SyncAllAsync(cancellationToken);
            }

            public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return SyncNowAsync(syncPairId, SyncRunRequest.Full, cancellationToken);
            }

            public Task SyncNowAsync(Guid syncPairId, SyncRunRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncNowCallCount++;
                LastSyncNowPairId = syncPairId;
                LastRequest = request;
                Requests.Add(request);
                _syncRequested.TrySetResult();
                if (BlockSyncNow)
                {
                    return _releaseSyncNow.Task;
                }

                return SyncNowExceptions.TryDequeue(out Exception? exception)
                    ? Task.FromException(exception)
                    : Task.CompletedTask;
            }

            public async Task<bool> WaitForSyncAsync(TimeSpan timeout)
            {
                Task completed = await Task.WhenAny(_syncRequested.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == _syncRequested.Task;
            }

            public async Task<bool> WaitForSyncCallCountAsync(int expectedCount, TimeSpan timeout)
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
                while (SyncNowCallCount < expectedCount && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                }

                return SyncNowCallCount >= expectedCount;
            }

            public void ReleaseSyncNow()
            {
                _releaseSyncNow.TrySetResult();
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
    }
}
