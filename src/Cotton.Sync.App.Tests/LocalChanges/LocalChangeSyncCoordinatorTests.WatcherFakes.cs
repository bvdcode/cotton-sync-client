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
        private class MutableTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow()
            {
                return _utcNow;
            }

            public void Advance(TimeSpan duration)
            {
                _utcNow = _utcNow.Add(duration);
            }
        }

        private class BlockingLocalChangeSuppression : ILocalChangeSuppression
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
            }

            public void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath)
            {
            }

            public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
            {
                return NoopDisposable.Instance;
            }

            public bool ShouldSuppress(LocalSyncRootChange change)
            {
                Entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
                return false;
            }

            public void Release()
            {
                _release.TrySetResult();
            }
        }

        private class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private class FakeWatcherFactory : ILocalSyncRootWatcherFactory
        {
            public Dictionary<Guid, FakeWatcher> CreatedWatchers { get; } = [];

            public Guid? FailingStartPairId { get; set; }

            public ILocalSyncRootWatcher Create(SyncPairSettings syncPair)
            {
                FakeWatcher watcher = new FakeWatcher(syncPair.Id);
                if (syncPair.Id == FailingStartPairId)
                {
                    watcher.StartException = new InvalidOperationException("Watcher failed to start.");
                }

                CreatedWatchers.Add(syncPair.Id, watcher);
                return watcher;
            }
        }

        private class FakeWatcher : ILocalSyncRootWatcher
        {
            private readonly Guid _syncPairId;

            public FakeWatcher(Guid syncPairId)
            {
                _syncPairId = syncPairId;
            }

            public event EventHandler<LocalSyncRootChange>? Changed;

            public Exception? StartException { get; set; }

            public int DisposeAsyncCallCount { get; private set; }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCallCount++;
                return ValueTask.CompletedTask;
            }

            public void Raise(string fullPath, LocalSyncRootChangeKind kind = LocalSyncRootChangeKind.Changed)
            {
                Changed?.Invoke(this, new LocalSyncRootChange(
                    _syncPairId,
                    fullPath,
                    kind));
            }

            public void RaiseRename(string oldFullPath, string fullPath, LocalSyncRootChangeKind kind = LocalSyncRootChangeKind.Renamed)
            {
                Changed?.Invoke(this, new LocalSyncRootChange(
                    _syncPairId,
                    fullPath,
                    kind,
                    oldFullPath));
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartCallCount++;
                if (StartException is not null)
                {
                    throw StartException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StopCallCount++;
                return Task.CompletedTask;
            }
        }

        private class FakeOfflineChangeDetector : ILocalOfflineChangeDetector
        {
            private readonly Exception? _exception;
            private readonly SyncRunRequest? _request;

            public FakeOfflineChangeDetector(SyncRunRequest? request)
            {
                _request = request;
            }

            public FakeOfflineChangeDetector(Exception exception)
            {
                _exception = exception;
            }

            public List<Guid> DetectedPairs { get; } = [];

            public Task<SyncRunRequest?> DetectAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DetectedPairs.Add(syncPair.Id);
                return _exception is null
                    ? Task.FromResult(_request)
                    : Task.FromException<SyncRunRequest?>(_exception);
            }
        }
    }
}
