// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesSyncPairDeletionHandlerTests
    {
        [Test]
        public async Task BeforeDeleteAsync_UnregistersWindowsVirtualFilesSyncRoot()
        {
            var adapter = new FakeCloudFilesAdapter();
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter);
            SyncPairSettings syncPair = CreatePair(SyncPairMode.WindowsVirtualFiles);

            await handler.BeforeDeleteAsync(syncPair);

            Assert.That(adapter.UnregisteredPairs.Select(static pair => pair.Id), Is.EqualTo(new[] { syncPair.Id }));
        }

        [Test]
        public async Task BeforeDeleteAsync_SkipsFullMirrorSyncPair()
        {
            var adapter = new FakeCloudFilesAdapter();
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter);

            await handler.BeforeDeleteAsync(CreatePair(SyncPairMode.FullMirror));

            Assert.That(adapter.UnregisteredPairs, Is.Empty);
        }

        [Test]
        public void BeforeDeleteAsync_HonorsCancellationBeforeNativeCleanup()
        {
            var adapter = new FakeCloudFilesAdapter();
            var handler = new WindowsCloudFilesSyncPairDeletionHandler(adapter);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.That(
                async () => await handler.BeforeDeleteAsync(CreatePair(SyncPairMode.WindowsVirtualFiles), cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(adapter.UnregisteredPairs, Is.Empty);
        }

        private static SyncPairSettings CreatePair(SyncPairMode mode)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Documents",
                LocalRootPath = @"S:\CottonSyncVfsQa\root",
                RemoteDisplayPath = "/Documents",
                RemoteRootNodeId = Guid.NewGuid(),
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
            };
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<SyncPairSettings> UnregisteredPairs { get; } = [];

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                UnregisteredPairs.Add(syncPair);
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }
    }
}
