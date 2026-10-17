// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.App.SyncPairs;
using static Cotton.Sync.Desktop.Tests.Platform.WindowsCloudFilesHydrationCoordinatorTests;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class WindowsCloudFilesHydrationPreparationTests
    {
        private string _directory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cotton-prepared-content-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task HydratePlaceholderAsync_AlreadyAvailableContentDoesNotDownloadAgain()
        {
            FakeContentProvider provider = new([]);
            WindowsCloudFilesHydrationCoordinator coordinator = new(provider, new FakeCloudFilesNativeApi(), _directory);
            WindowsVirtualFilesDehydrationPairWorkTests.FakeCloudFilesAdapter adapter = new();
            WindowsCloudFilesHydrator hydrator = new(adapter, coordinator);
            SyncPairSettings pair = new() { LocalRootPath = _directory };

            await hydrator.HydratePlaceholderAsync(pair, "already-downloaded.bin");

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Is.Empty);
                Assert.That(adapter.HydratedPaths, Is.EqualTo(new[] { "already-downloaded.bin" }));
                Assert.That(Directory.GetFiles(_directory), Is.Empty);
            });
        }

        [TestCase(0, 16)]
        [TestCase(4, 6)]
        public async Task PrepareHydrationAsync_ServesReadyContentWithoutAnotherDownload(int offset, int length)
        {
            byte[] content = "0123456789abcdef"u8.ToArray();
            FakeContentProvider provider = new(content);
            FakeCloudFilesNativeApi native = new();
            WindowsCloudFilesHydrationCoordinator coordinator = new(provider, native, _directory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset, length);
            using (IDisposable prepared = await coordinator.PrepareHydrationAsync(
                WindowsCloudFilesPlaceholderIdentity.Parse(request.FileIdentity)))
            {
                Assert.Multiple(() =>
                {
                    Assert.That(native.Transfers, Is.Empty);
                    Assert.That(native.ProviderProgress, Is.Empty);
                    Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
                });
                await coordinator.HandleFetchDataAsync(request);
                Assert.Multiple(() =>
                {
                    Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(1));
                    Assert.That(native.Transfers.Single().Buffer, Is.EqualTo(content.AsSpan(offset, length).ToArray()));
                });
            }
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public async Task PrepareHydrationAsync_DoesNotReuseDifferentVersion()
        {
            byte[] content = "same length data"u8.ToArray();
            FakeContentProvider provider = new(content);
            FakeCloudFilesNativeApi native = new();
            WindowsCloudFilesHydrationCoordinator coordinator = new(provider, native, _directory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, 0, content.Length);
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(request.FileIdentity);
            using IDisposable prepared = await coordinator.PrepareHydrationAsync(identity);
            WindowsCloudFilesPlaceholderIdentity updated = identity with { ETag = "updated-version", FileManifestId = Guid.NewGuid() };
            await coordinator.HandleFetchDataAsync(request with { FileIdentity = updated.ToBytes() });
            Assert.That(provider.DownloadedIdentities, Is.EqualTo(new[] { identity, updated }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PrepareHydrationAsync_OverlappingLeasesRemainUsableAfterOneIsDisposed(bool disposeNewest)
        {
            byte[] content = "ready data"u8.ToArray();
            FakeContentProvider provider = new(content);
            FakeCloudFilesNativeApi native = new();
            WindowsCloudFilesHydrationCoordinator coordinator = new(provider, native, _directory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, 0, content.Length);
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(request.FileIdentity);
            using IDisposable first = await coordinator.PrepareHydrationAsync(identity);
            using (IDisposable second = await coordinator.PrepareHydrationAsync(identity))
            {
                if (disposeNewest)
                {
                    second.Dispose();
                }
                else
                {
                    first.Dispose();
                }
                await coordinator.HandleFetchDataAsync(request);
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(2));
            }
            first.Dispose();
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        }

        [Test]
        public void PrepareHydrationAsync_InvalidContentIsRemovedBeforeNativeRequest()
        {
            byte[] content = "expected"u8.ToArray();
            FakeCloudFilesNativeApi native = new();
            WindowsCloudFilesHydrationCoordinator coordinator = new(new FakeContentProvider("invalid!"u8.ToArray()), native, _directory);
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(CreateFetchRequest(content, 0, content.Length).FileIdentity);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.PrepareHydrationAsync(identity));
            Assert.Multiple(() =>
            {
                Assert.That(native.Transfers, Is.Empty);
                Assert.That(Directory.GetFiles(_directory), Is.Empty);
            });
        }

        [Test]
        public async Task PrepareHydrationAsync_CancellationRemovesPartialContent()
        {
            byte[] content = "expected"u8.ToArray();
            TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            BlockingPreparedContentProvider provider = new(ready);
            FakeCloudFilesNativeApi native = new();
            WindowsCloudFilesHydrationCoordinator coordinator = new(provider, native, _directory);
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(CreateFetchRequest(content, 0, content.Length).FileIdentity);
            using CancellationTokenSource cancellation = new();
            Task<IDisposable> preparing = coordinator.PrepareHydrationAsync(identity, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            Assert.CatchAsync<OperationCanceledException>(async () => await preparing);
            Assert.Multiple(() =>
            {
                Assert.That(native.Transfers, Is.Empty);
                Assert.That(Directory.GetFiles(_directory), Is.Empty);
            });
        }
    }
}
