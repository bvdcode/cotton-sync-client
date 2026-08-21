// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private class RecordingRenameSyncSupervisor : ISyncSupervisor
        {
            private readonly TaskCompletionSource<SyncRunRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _syncNowCallCount;

            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public int SyncNowCallCount => Volatile.Read(ref _syncNowCallCount);

            public Task<SyncRunRequest> WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken)
            {
                return _request.Task.WaitAsync(timeout, cancellationToken);
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task StartAsync(bool startPaused, CancellationToken cancellationToken = default)
            {
                return StartAsync(cancellationToken);
            }

            public Task SyncAllAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task SyncAllAsync(SyncRunRequest request, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                return SyncAllAsync(cancellationToken);
            }

            public Task SyncNowAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return SyncNowAsync(syncPairId, SyncRunRequest.Full, cancellationToken);
            }

            public Task SyncNowAsync(
                Guid syncPairId,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(request);
                Interlocked.Increment(ref _syncNowCallCount);
                _request.TrySetResult(request);
                return Task.CompletedTask;
            }

            public Task PauseAllAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task PauseAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task ResumeAllAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task ResumeAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        private class NoopSyncSupervisor : ISyncSupervisor
        {
            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StartAsync(bool startPaused, CancellationToken cancellationToken = default)
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
                return Task.CompletedTask;
            }

            public Task SyncNowAsync(
                Guid syncPairId,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return SyncNowAsync(syncPairId, cancellationToken);
            }

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

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class NoopCloudFilesCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }

        private class SingleSyncPairSettingsStore : ISyncPairSettingsStore
        {
            private SyncPairSettings? _syncPair;

            public SingleSyncPairSettingsStore(SyncPairSettings syncPair)
            {
                _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<SyncPairSettings> result = _syncPair is null ? [] : [_syncPair];
                return Task.FromResult(result);
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncPairSettings? result = _syncPair is not null && _syncPair.Id == syncPairId
                    ? _syncPair
                    : null;
                return Task.FromResult(result);
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _syncPair = syncPair ?? throw new ArgumentNullException(nameof(syncPair));
                return Task.CompletedTask;
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_syncPair is not null && _syncPair.Id == syncPairId)
                {
                    _syncPair = null;
                }

                return Task.CompletedTask;
            }
        }
    }
}
