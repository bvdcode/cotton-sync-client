// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        private class FakeSyncSupervisor : ISyncSupervisor
        {
            private readonly ICollection<string>? _calls;

            public FakeSyncSupervisor(ICollection<string>? calls = null)
            {
                _calls = calls;
            }

            public IReadOnlyList<SyncPairStatus> CurrentStatuses => [];

            public Guid? LastSyncNowPairId { get; private set; }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public int SyncNowCallCount { get; private set; }

            public int PauseAllCallCount { get; private set; }

            public bool LastStartPaused { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                return StartAsync(startPaused: false, cancellationToken);
            }

            public Task StartAsync(bool startPaused, CancellationToken cancellationToken = default)
            {
                StartCallCount++;
                LastStartPaused = startPaused;
                _calls?.Add("supervisor:start");
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
                SyncNowCallCount++;
                LastSyncNowPairId = syncPairId;
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
                PauseAllCallCount++;
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
                StopCallCount++;
                _calls?.Add("supervisor:stop");
                return Task.CompletedTask;
            }
        }

        private class FakePlatformCommandService : IPlatformCommandService
        {
            public string? LastOpenedFolder { get; private set; }

            public Uri? LastOpenedUrl { get; private set; }

            public int OpenFolderCallCount { get; private set; }

            public int OpenWebCallCount { get; private set; }

            public Task OpenFolderAsync(string localPath, CancellationToken cancellationToken = default)
            {
                OpenFolderCallCount++;
                LastOpenedFolder = localPath;
                return Task.CompletedTask;
            }

            public Task OpenWebAsync(Uri url, CancellationToken cancellationToken = default)
            {
                OpenWebCallCount++;
                LastOpenedUrl = url;
                return Task.CompletedTask;
            }
        }

        private class FakeLocalChangeSyncCoordinator : ILocalChangeSyncCoordinator
        {
            private readonly ICollection<string>? _calls;

            public FakeLocalChangeSyncCoordinator(ICollection<string>? calls = null)
            {
                _calls = calls;
            }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public Exception? StartException { get; init; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                StartCallCount++;
                _calls?.Add("local:start");
                if (StartException is not null)
                {
                    throw StartException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCallCount++;
                _calls?.Add("local:stop");
                return Task.CompletedTask;
            }
        }

        private class FakeRemoteChangeSyncCoordinator : IRemoteChangeSyncCoordinator
        {
            private readonly ICollection<string>? _calls;

            public FakeRemoteChangeSyncCoordinator(ICollection<string>? calls = null)
            {
                _calls = calls;
            }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public Exception? StartException { get; init; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                StartCallCount++;
                _calls?.Add("remote:start");
                if (StartException is not null)
                {
                    throw StartException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCallCount++;
                _calls?.Add("remote:stop");
                return Task.CompletedTask;
            }
        }

        private class FakePeriodicSyncCoordinator : IPeriodicSyncCoordinator
        {
            private readonly ICollection<string>? _calls;

            public FakePeriodicSyncCoordinator(ICollection<string>? calls = null)
            {
                _calls = calls;
            }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public Exception? StartException { get; init; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                StartCallCount++;
                _calls?.Add("periodic:start");
                if (StartException is not null)
                {
                    throw StartException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCallCount++;
                _calls?.Add("periodic:stop");
                return Task.CompletedTask;
            }
        }

        private class FakeSyncCoreLifecycleComponent : ISyncCoreLifecycleComponent
        {
            private readonly ICollection<string> _calls;
            private readonly string _name;

            public FakeSyncCoreLifecycleComponent(string name, ICollection<string> calls)
            {
                _name = name;
                _calls = calls;
            }

            public Exception? StartException { get; set; }

            public string Name => _name;

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                StartCallCount++;
                _calls.Add(_name + ":start");
                if (StartException is not null)
                {
                    throw StartException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCallCount++;
                _calls.Add(_name + ":stop");
                return Task.CompletedTask;
            }
        }

        private class FakeSyncPairDeletionHandler : ISyncPairDeletionHandler
        {
            private readonly ICollection<string> _calls;

            public FakeSyncPairDeletionHandler(ICollection<string> calls)
            {
                _calls = calls;
            }

            public List<SyncPairSettings> DeletedPairs { get; } = [];

            public Exception? Exception { get; set; }

            public Task BeforeDeleteAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                _calls.Add("deletion-handler:before-delete");
                DeletedPairs.Add(syncPair);
                if (Exception is not null)
                {
                    throw Exception;
                }

                return Task.CompletedTask;
            }
        }
    }
}
