// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesHydrationCoordinatorTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-hydration-" + Guid.NewGuid().ToString("N"));
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
        public async Task HandleFetchDataAsync_DownloadsAndTransfersRequestedRange()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            var provider = new FakeContentProvider(content);
            var nativeApi = new FakeCloudFilesNativeApi();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 4, length: 6);

            await coordinator.HandleFetchDataAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(1));
                Assert.That(provider.DownloadedIdentities[0].NodeFileId, Is.EqualTo(Guid.Parse("33333333-3333-3333-3333-333333333333")));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(nativeApi.Transfers[0].Offset, Is.EqualTo(4));
                Assert.That(nativeApi.Transfers[0].Length, Is.EqualTo(6));
                Assert.That(Encoding.UTF8.GetString(nativeApi.Transfers[0].Buffer), Is.EqualTo("456789"));
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_ReportsFailureWhenContentHashDoesNotMatch()
        {
            byte[] expectedContent = Encoding.UTF8.GetBytes("expected");
            var provider = new FakeContentProvider(Encoding.UTF8.GetBytes("mismatch"));
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(expectedContent, offset: 0, length: expectedContent.Length);

            await coordinator.HandleFetchDataAsync(request);
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(nativeApi.Transfers[0].Offset, Is.EqualTo(0));
                Assert.That(nativeApi.Transfers[0].Length, Is.EqualTo(expectedContent.Length));
                Assert.That(diagnostic.Operation, Is.EqualTo("hydrate"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(diagnostic.Details, Does.Contain("hash"));
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_ReportsFailureWhenContentSizeDoesNotMatch()
        {
            byte[] expectedContent = Encoding.UTF8.GetBytes("expected");
            var provider = new FakeContentProvider(Encoding.UTF8.GetBytes("short"));
            var nativeApi = new FakeCloudFilesNativeApi();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(expectedContent, offset: 0, length: expectedContent.Length);

            await coordinator.HandleFetchDataAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(nativeApi.Transfers[0].Offset, Is.EqualTo(0));
                Assert.That(nativeApi.Transfers[0].Length, Is.EqualTo(expectedContent.Length));
            });
        }

        [Test]
        public void HandleFetchDataAsync_PropagatesCancellationWithoutFailureTransfer()
        {
            var provider = new CanceledContentProvider();
            var nativeApi = new FakeCloudFilesNativeApi();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            byte[] content = Encoding.UTF8.GetBytes("cancel");
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            Assert.ThrowsAsync<OperationCanceledException>(() =>
                coordinator.HandleFetchDataAsync(request, new CancellationToken(canceled: true)));

            Assert.That(nativeApi.Transfers, Is.Empty);
        }

        private static WindowsCloudFilesFetchDataRequest CreateFetchRequest(byte[] content, long offset, long length)
        {
            RemoteFilePlaceholderRequest placeholder = CreatePlaceholderRequest(content);
            string normalizedPath = SyncPath.Normalize(placeholder.RelativePath);
            byte[] identity = WindowsCloudFilesPlaceholderIdentity
                .Create(placeholder, normalizedPath)
                .ToBytes();

            return new WindowsCloudFilesFetchDataRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(2),
                new WindowsCloudFilesRequestKey(3),
                identity,
                content.Length,
                offset,
                length,
                offset,
                length,
                @"\Device\HarddiskVolume1\Cotton\remote-only.txt",
                10);
        }

        private static RemoteFilePlaceholderRequest CreatePlaceholderRequest(byte[] content)
        {
            return new RemoteFilePlaceholderRequest(
                "11111111-1111-1111-1111-111111111111",
                @"S:\CottonSync",
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "remote-only.txt",
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = "remote-only.txt",
                    ContentType = "text/plain",
                    SizeBytes = content.Length,
                    ContentHash = Convert.ToHexStringLower(SHA256.HashData(content)),
                    ETag = "etag",
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string>(),
                });
        }

        private sealed class FakeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public FakeContentProvider(byte[] content)
            {
                _content = content;
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class CanceledContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            public Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private sealed class FakeCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
            {
                return new WindowsCloudFilesConnection(
                    request.LocalRootPath,
                    new WindowsCloudFilesConnectionKey(1),
                    DisconnectSyncRoot);
            }

            public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
            {
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                Transfers.Add(transfer with { Buffer = transfer.Buffer.ToArray() });
            }
        }
    }
}
