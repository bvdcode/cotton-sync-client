// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesSyncRootConnectionCoordinatorTests
    {
        [Test]
        public async Task StartAsync_ConnectsOnlyEnabledWindowsVirtualFilesPairs()
        {
            SyncPairSettings fullMirror = CreatePair("Full", @"S:\CottonFull", SyncPairMode.FullMirror);
            SyncPairSettings disabledVfs = CreatePair("Disabled", @"S:\CottonDisabled", SyncPairMode.WindowsVirtualFiles, isEnabled: false);
            SyncPairSettings enabledVfs = CreatePair("Virtual", @"S:\CottonVirtual", SyncPairMode.WindowsVirtualFiles);
            var store = new FakeSyncPairSettingsStore([fullMirror, disabledVfs, enabledVfs]);
            var adapter = new FakeCloudFilesAdapter();
            var coordinator = new WindowsCloudFilesSyncRootConnectionCoordinator(
                store,
                adapter,
                new RecordingCallbackHandler());

            await coordinator.StartAsync();

            Assert.Multiple(() =>
            {
                Assert.That(store.InitializeCallCount, Is.EqualTo(1));
                Assert.That(adapter.ConnectedPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { enabledVfs.Id }));
                Assert.That(adapter.ConnectedHandlers, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task StopAsync_DisconnectsConnectedRoots()
        {
            SyncPairSettings first = CreatePair("First", @"S:\CottonFirst", SyncPairMode.WindowsVirtualFiles);
            SyncPairSettings second = CreatePair("Second", @"S:\CottonSecond", SyncPairMode.WindowsVirtualFiles);
            var adapter = new FakeCloudFilesAdapter();
            var coordinator = new WindowsCloudFilesSyncRootConnectionCoordinator(
                new FakeSyncPairSettingsStore([first, second]),
                adapter,
                new RecordingCallbackHandler());
            await coordinator.StartAsync();

            await coordinator.StopAsync();
            await coordinator.StopAsync();

            Assert.That(adapter.DisconnectedKeys, Is.EquivalentTo(new[]
            {
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesConnectionKey(2),
            }));
        }

        [Test]
        public void StartAsync_StopsBeforeConnectingRootsWhenCanceled()
        {
            SyncPairSettings syncPair = CreatePair("Virtual", @"S:\CottonVirtual", SyncPairMode.WindowsVirtualFiles);
            var adapter = new FakeCloudFilesAdapter();
            var coordinator = new WindowsCloudFilesSyncRootConnectionCoordinator(
                new FakeSyncPairSettingsStore([syncPair]),
                adapter,
                new RecordingCallbackHandler());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.That(
                async () => await coordinator.StartAsync(cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());

            Assert.That(adapter.ConnectedPairs, Is.Empty);
        }

        [Test]
        public void StartAsync_RollsBackConnectedRootsWhenLaterConnectFails()
        {
            SyncPairSettings first = CreatePair("First", @"S:\CottonFirst", SyncPairMode.WindowsVirtualFiles);
            SyncPairSettings second = CreatePair("Second", @"S:\CottonSecond", SyncPairMode.WindowsVirtualFiles);
            var startupError = new InvalidOperationException("Cloud Files connect failed.");
            var adapter = new FakeCloudFilesAdapter
            {
                ThrowOnConnectionNumber = 2,
                ConnectException = startupError,
            };
            var coordinator = new WindowsCloudFilesSyncRootConnectionCoordinator(
                new FakeSyncPairSettingsStore([first, second]),
                adapter,
                new RecordingCallbackHandler());

            InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.StartAsync())!;

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.SameAs(startupError));
                Assert.That(adapter.ConnectedPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { first.Id, second.Id }));
                Assert.That(adapter.DisconnectedKeys, Is.EqualTo(new[] { new WindowsCloudFilesConnectionKey(1) }));
            });
        }

        private static SyncPairSettings CreatePair(
            string name,
            string localRootPath,
            SyncPairMode mode,
            bool isEnabled = true)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = name,
                LocalRootPath = localRootPath,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/" + name,
                IsEnabled = isEnabled,
                Mode = mode,
                CreatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
            };
        }

        private sealed class FakeSyncPairSettingsStore : ISyncPairSettingsStore
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
                return Task.FromResult(_syncPairs.FirstOrDefault(pair => pair.Id == syncPairId));
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

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            private long _nextConnectionKey = 1;

            public List<SyncPairSettings> ConnectedPairs { get; } = [];

            public List<IWindowsCloudFilesCallbackHandler> ConnectedHandlers { get; } = [];

            public List<WindowsCloudFilesConnectionKey> DisconnectedKeys { get; } = [];

            public int? ThrowOnConnectionNumber { get; init; }

            public Exception? ConnectException { get; init; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                throw new NotSupportedException();
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                ConnectedPairs.Add(syncPair);
                ConnectedHandlers.Add(callbackHandler);
                if (ThrowOnConnectionNumber == ConnectedPairs.Count)
                {
                    throw ConnectException ?? new InvalidOperationException("Connect failed.");
                }

                var key = new WindowsCloudFilesConnectionKey(_nextConnectionKey++);
                return new WindowsCloudFilesConnection(syncPair.LocalRootPath, key, DisconnectedKeys.Add);
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class RecordingCallbackHandler : IWindowsCloudFilesCallbackHandler
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
    }
}
