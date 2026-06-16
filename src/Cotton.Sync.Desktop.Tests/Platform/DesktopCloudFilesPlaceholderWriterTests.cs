// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Cotton.Files;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class DesktopCloudFilesPlaceholderWriterTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-placeholder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void CreatePlaceholderAsync_RejectsUnsupportedCloudFilesHost()
        {
            var writer = new DesktopCloudFilesPlaceholderWriter(
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(false, "Cloud Files is disabled."));

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.ThrowsAsync<RemoteFilePlaceholderUnavailableException>(
                    () => writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory)));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(exception?.Reason, Is.EqualTo("Cloud Files is disabled."));
            });
        }

        [Test]
        public void CreatePlaceholderAsync_RejectsUnsafeRootBeforeNativeCall()
        {
            var writer = new DesktopCloudFilesPlaceholderWriter(
                new WindowsVirtualFilesRootSafetyPolicy(
                    folder => folder == Environment.SpecialFolder.UserProfile ? @"C:\Users\Vadim" : string.Empty,
                    () => _tempDirectory),
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.ThrowsAsync<RemoteFilePlaceholderUnavailableException>(
                    () => writer.CreatePlaceholderAsync(CreateRequest(@"C:\Users\Vadim")));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(exception?.Reason, Does.Contain("user profile"));
            });
        }

        [Test]
        public async Task CreatePlaceholderAsync_CreatesPlaceholderThroughAdapter()
        {
            var adapter = new FakeCloudFilesAdapter();
            var writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));

            RemoteFilePlaceholderResult result = await writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory));

            Assert.Multiple(() =>
            {
                Assert.That(adapter.Requests, Has.Count.EqualTo(1));
                Assert.That(adapter.Requests[0].RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(result.PlaceholderIdentity, Is.EqualTo(adapter.PlaceholderIdentity));
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
            });
        }

        [Test]
        public void CreatePlaceholderAsync_StopsBeforeAdapterWhenCanceled()
        {
            var adapter = new FakeCloudFilesAdapter();
            var writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(
                () => writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory), cancellation.Token));

            Assert.That(adapter.Requests, Is.Empty);
        }

        [Test]
        public void CreatePlaceholderAsync_ReportsAdapterFailureAsPlaceholderUnavailable()
        {
            var adapter = new FakeCloudFilesAdapter
            {
                Exception = new WindowsCloudFilesNativeException("CfCreatePlaceholders", unchecked((int)0x8007017C)),
            };
            var writer = new DesktopCloudFilesPlaceholderWriter(
                cloudFilesAdapter: adapter,
                getCapabilities: () => new SyncPairModeCapabilitySnapshot(true, "Cloud Files available."));

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.ThrowsAsync<RemoteFilePlaceholderUnavailableException>(
                    () => writer.CreatePlaceholderAsync(CreateRequest(_tempDirectory)));

            Assert.Multiple(() =>
            {
                Assert.That(adapter.Requests, Has.Count.EqualTo(1));
                Assert.That(exception?.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(exception?.Reason, Does.Contain("CfCreatePlaceholders"));
            });
        }

        private static RemoteFilePlaceholderRequest CreateRequest(string localRootPath)
        {
            return new RemoteFilePlaceholderRequest(
                "pair-a",
                localRootPath,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "remote-only.txt",
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    NodeId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    FileManifestId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    OriginalNodeFileId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OwnerId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    Name = "remote-only.txt",
                    ContentType = "text/plain",
                    SizeBytes = 12,
                    ContentHash = "hash",
                    ETag = "etag",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = "remote-only.txt" },
                });
        }

        private sealed class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public byte[] PlaceholderIdentity { get; } = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];

            public List<RemoteFilePlaceholderRequest> Requests { get; } = [];

            public Exception? Exception { get; set; }

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                Requests.Add(request);
                if (Exception is not null)
                {
                    throw Exception;
                }

                return new RemoteFilePlaceholderResult(PlaceholderIdentity);
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
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
