// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.App.Tests.TestSupport;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Cotton.Sync.App.Tests.Supervision
{
    public partial class SyncSupervisorTests
    {

        [Test]
        public async Task StartAsync_StopsExistingRunnersBeforeReplacingThem()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairSettingsStore store = new FakeSyncPairSettingsStore([documents]);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            SyncSupervisor supervisor = new SyncSupervisor(store, factory, new InMemoryAppStatusPublisher());
            await supervisor.StartAsync();
            FakeSyncPairRunner firstRunner = factory.CreatedRunners[documents.Id];

            await supervisor.StartAsync();

            FakeSyncPairRunner secondRunner = factory.CreatedRunners[documents.Id];
            Assert.Multiple(() =>
            {
                Assert.That(firstRunner.StopCallCount, Is.EqualTo(1));
                Assert.That(secondRunner, Is.Not.SameAs(firstRunner));
                Assert.That(secondRunner.StartCallCount, Is.EqualTo(1));
                Assert.That(factory.AllCreatedRunners, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public async Task StartAsync_StopsCreatedRunnersWhenLaterRunnerFails()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: true);
            FakeSyncPairSettingsStore store = new FakeSyncPairSettingsStore([documents, pictures]);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory
            {
                FailingStartPairId = pictures.Id,
            };
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher(new SyncAppStatus(true, [], DateTime.UtcNow));
            SyncSupervisor supervisor = new SyncSupervisor(store, factory, publisher);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await supervisor.StartAsync());

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Is.EqualTo("Runner failed to start."));
                Assert.That(factory.CreatedRunners[documents.Id].StopCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedRunners[pictures.Id].StopCallCount, Is.EqualTo(1));
                Assert.That(supervisor.CurrentStatuses, Is.Empty);
                Assert.That(publisher.Current.SyncPairs, Is.Empty);
            });
        }

        [Test]
        public async Task StopAsync_StopsEveryRunnerPublishesDisabledStatusesAndClearsReferences()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            SyncPairSettings pictures = CreatePair("Pictures", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            InMemoryAppStatusPublisher publisher = new InMemoryAppStatusPublisher();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents, pictures]),
                factory,
                publisher);
            await supervisor.StartAsync();

            await supervisor.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(factory.CreatedRunners.Values.Select(runner => runner.StopCallCount), Is.All.EqualTo(1));
                Assert.That(
                    publisher.Current.SyncPairs.Select(status => status.State),
                    Is.All.EqualTo(SyncPairRunState.Disabled));
                Assert.That(supervisor.CurrentStatuses, Is.Empty);
            });
        }

        [Test]
        public async Task StopAsync_ReleasesRunnerReferencesForGarbageCollection()
        {
            (WeakReference runnerReference, WeakReference payloadReference) =
                await CreateStoppedRunnerWeakReferencesAsync();

            ForceFullCollection();

            Assert.Multiple(() =>
            {
                Assert.That(runnerReference.IsAlive, Is.False);
                Assert.That(payloadReference.IsAlive, Is.False);
            });
        }

        [Test]
        public async Task StopAsync_ReachesRunnerWhileSyncAllIsRunning()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            FakeSyncPairRunnerFactory factory = new FakeSyncPairRunnerFactory();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                new InMemoryAppStatusPublisher());
            await supervisor.StartAsync();
            FakeSyncPairRunner runner = factory.CreatedRunners[documents.Id];
            runner.BlockSyncNow = true;

            Task syncAll = supervisor.SyncAllAsync();
            await runner.WaitForSyncNowAsync(TimeSpan.FromSeconds(2));
            Task stop = supervisor.StopAsync();
            bool stopReachedRunner = await runner.WaitForStopAsync(TimeSpan.FromMilliseconds(250));
            runner.ReleaseSyncNow();
            await Task.WhenAll(syncAll, stop).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(stopReachedRunner, Is.True);
                Assert.That(runner.SyncNowCallCount, Is.EqualTo(1));
                Assert.That(runner.StopCallCount, Is.EqualTo(1));
                Assert.That(runner.Status.State, Is.EqualTo(SyncPairRunState.Disabled));
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<(WeakReference RunnerReference, WeakReference PayloadReference)>
            CreateStoppedRunnerWeakReferencesAsync()
        {
            SyncPairSettings documents = CreatePair("Documents", isEnabled: true);
            WeakReferenceRunnerFactory factory = new WeakReferenceRunnerFactory();
            SyncSupervisor supervisor = new SyncSupervisor(
                new FakeSyncPairSettingsStore([documents]),
                factory,
                new InMemoryAppStatusPublisher());

            await supervisor.StartAsync();
            await supervisor.StopAsync();

            return (factory.RunnerReference!, factory.PayloadReference!);
        }

        private static void ForceFullCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static async Task WaitForStatusCountAsync(
            RecordingObserver<SyncAppStatus> observer,
            Func<SyncAppStatus, bool> predicate,
            int minimumCount,
            TimeSpan timeout)
        {
            DateTime deadlineUtc = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                if (observer.Values.Count(predicate) >= minimumCount)
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail("Timed out waiting for status count " + minimumCount.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static SyncPairSettings CreatePair(string displayName, bool isEnabled)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                LocalRootPath = "/home/user/" + displayName,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/" + displayName,
                IsEnabled = isEnabled,
                Mode = SyncPairMode.FullMirror,
            };
        }

        private class FakeSyncPairSettingsStore : ISyncPairSettingsStore
        {
            private readonly IReadOnlyList<SyncPairSettings> _syncPairs;

            public FakeSyncPairSettingsStore(IReadOnlyList<SyncPairSettings> syncPairs)
            {
                _syncPairs = syncPairs;
            }

            public int InitializeCallCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                InitializeCallCount++;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncPairSettings>> ListAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_syncPairs);
            }

            public Task<SyncPairSettings?> GetAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_syncPairs.SingleOrDefault(syncPair => syncPair.Id == syncPairId));
            }

            public Task UpsertAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(Guid syncPairId, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class FakeSyncPairRunnerFactory : ISyncPairRunnerFactory
        {
            public Dictionary<Guid, FakeSyncPairRunner> CreatedRunners { get; } = [];

            public List<FakeSyncPairRunner> AllCreatedRunners { get; } = [];

            public Guid? FailingStartPairId { get; set; }

            public ISyncPairRunner Create(SyncPairSettings syncPair)
            {
                FakeSyncPairRunner runner = new FakeSyncPairRunner(syncPair);
                if (syncPair.Id == FailingStartPairId)
                {
                    runner.StartException = new InvalidOperationException("Runner failed to start.");
                }

                CreatedRunners[syncPair.Id] = runner;
                AllCreatedRunners.Add(runner);
                return runner;
            }
        }

        private class FakeSyncPairRunner : ISyncPairRunner
        {
            private readonly SyncPairSettings _syncPair;
            private readonly TaskCompletionSource _stopStarted = CreateCompletionSource();
            private readonly TaskCompletionSource _syncNowRelease = CreateCompletionSource();
            private readonly TaskCompletionSource _syncNowStarted = CreateCompletionSource();
            private SyncPairRunState _state;

            public FakeSyncPairRunner(SyncPairSettings syncPair)
            {
                _syncPair = syncPair;
                _state = syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled;
            }

            public int PauseCallCount { get; private set; }

            public int ResumeCallCount { get; private set; }

            public int StartCallCount { get; private set; }

            public int StopCallCount { get; private set; }

            public int SyncNowCallCount { get; private set; }

            public SyncRunRequest? LastSyncRequest { get; private set; }

            public bool BlockSyncNow { get; set; }

            public Exception? StartException { get; set; }

            public Exception? SyncNowException { get; set; }

            public Guid SyncPairId => _syncPair.Id;

            public SyncPairStatus Status => new(
                _syncPair.Id,
                _syncPair.DisplayName,
                _state,
                null,
                null,
                DateTime.UtcNow);

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                StartCallCount++;
                if (StartException is not null)
                {
                    throw StartException;
                }

                _state = _syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled;
                return Task.CompletedTask;
            }

            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                PauseCallCount++;
                _state = SyncPairRunState.Paused;
                return Task.CompletedTask;
            }

            public Task ResumeAsync(CancellationToken cancellationToken = default)
            {
                ResumeCallCount++;
                _state = _syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled;
                return Task.CompletedTask;
            }

            public async Task SyncNowAsync(CancellationToken cancellationToken = default)
            {
                SyncNowCallCount++;
                _state = SyncPairRunState.Syncing;
                _syncNowStarted.TrySetResult();
                if (BlockSyncNow)
                {
                    await _syncNowRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (SyncNowException is not null)
                {
                    _state = SyncPairRunState.Error;
                    throw SyncNowException;
                }

                if (_state == SyncPairRunState.Syncing)
                {
                    _state = SyncPairRunState.Idle;
                }
            }

            public Task SyncNowAsync(
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                LastSyncRequest = request;
                return SyncNowAsync(cancellationToken);
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                StopCallCount++;
                _stopStarted.TrySetResult();
                _state = SyncPairRunState.Disabled;
                return Task.CompletedTask;
            }

            public void ReleaseSyncNow()
            {
                _syncNowRelease.TrySetResult();
            }

            public async Task<bool> WaitForStopAsync(TimeSpan timeout)
            {
                try
                {
                    await _stopStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public Task WaitForSyncNowAsync(TimeSpan timeout)
            {
                return _syncNowStarted.Task.WaitAsync(timeout);
            }

            private static TaskCompletionSource CreateCompletionSource()
            {
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private class WeakReferenceRunnerFactory : ISyncPairRunnerFactory
        {
            public WeakReference? RunnerReference { get; private set; }

            public WeakReference? PayloadReference { get; private set; }

            public ISyncPairRunner Create(SyncPairSettings syncPair)
            {
                byte[] payload = new byte[8 * 1024 * 1024];
                MemoryProbeSyncPairRunner runner = new MemoryProbeSyncPairRunner(syncPair, payload);
                RunnerReference = new WeakReference(runner);
                PayloadReference = new WeakReference(payload);
                return runner;
            }
        }

        private class MemoryProbeSyncPairRunner : ISyncPairRunner
        {
            private readonly byte[] _payload;
            private readonly SyncPairSettings _syncPair;
            private SyncPairRunState _state;

            public MemoryProbeSyncPairRunner(SyncPairSettings syncPair, byte[] payload)
            {
                _syncPair = syncPair;
                _payload = payload;
                _state = syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled;
            }

            public Guid SyncPairId => _syncPair.Id;

            public SyncPairStatus Status => new(
                _syncPair.Id,
                _syncPair.DisplayName,
                _state,
                null,
                null,
                DateTime.UtcNow);

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                _state = _syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled;
                GC.KeepAlive(_payload);
                return Task.CompletedTask;
            }

            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                _state = SyncPairRunState.Paused;
                return Task.CompletedTask;
            }

            public Task ResumeAsync(CancellationToken cancellationToken = default)
            {
                _state = _syncPair.IsEnabled ? SyncPairRunState.Idle : SyncPairRunState.Disabled;
                return Task.CompletedTask;
            }

            public Task SyncNowAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task SyncNowAsync(
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return SyncNowAsync(cancellationToken);
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                _state = SyncPairRunState.Disabled;
                return Task.CompletedTask;
            }
        }
    }
}
