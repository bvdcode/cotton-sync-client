// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.Progress;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesHydrationCoordinatorTests
    {
        [Test]
        public async Task QueueFetchData_StartsDeepTreeHydrationWithoutSyncTreeWork()
        {
            byte[] content = Encoding.UTF8.GetBytes("small");
            BlockingStartContentProvider provider = new BlockingStartContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            using WindowsCloudFilesCallbackDispatcher dispatcher = new WindowsCloudFilesCallbackDispatcher(
                coordinator,
                nativeApi.TransferData,
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 4));
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(
                content,
                offset: 0,
                length: content.Length,
                requestKey: 100_000,
                relativePath: "HugeTree/099/file-099999.txt");

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool accepted = dispatcher.QueueFetchData(request);
            WindowsCloudFilesPlaceholderIdentity startedIdentity =
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            stopwatch.Stop();

            provider.Release();
            await WaitUntilAsync(() => nativeApi.Transfers.Count == 1).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.True);
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
                Assert.That(startedIdentity.RelativePath, Is.EqualTo("HugeTree/099/file-099999.txt"));
                Assert.That(nativeApi.Transfers[0].RequestKey, Is.EqualTo(request.RequestKey));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(Encoding.UTF8.GetString(nativeApi.Transfers[0].Buffer), Is.EqualTo("small"));
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_ReportsFailureWhenContentHashDoesNotMatch()
        {
            byte[] expectedContent = Encoding.UTF8.GetBytes("expected");
            FakeContentProvider provider = new FakeContentProvider(Encoding.UTF8.GetBytes("mismatch"));
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
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
            FakeContentProvider provider = new FakeContentProvider(Encoding.UTF8.GetBytes("short"));
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
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
        public async Task HandleFetchDataAsync_AllowsRetryAfterFailedHydration()
        {
            byte[] expectedContent = Encoding.UTF8.GetBytes("expected");
            SequencedContentProvider provider = new SequencedContentProvider(
                Encoding.UTF8.GetBytes("mismatch"),
                expectedContent);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            WindowsCloudFilesFetchDataRequest failedAttempt =
                CreateFetchRequest(expectedContent, offset: 0, length: expectedContent.Length, requestKey: 3);
            WindowsCloudFilesFetchDataRequest retryAttempt =
                CreateFetchRequest(expectedContent, offset: 0, length: expectedContent.Length, requestKey: 4);

            await coordinator.HandleFetchDataAsync(failedAttempt);
            await coordinator.HandleFetchDataAsync(retryAttempt);

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(2));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(2));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(nativeApi.Transfers[1].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(nativeApi.Transfers[1].RequestKey, Is.EqualTo(retryAttempt.RequestKey));
                Assert.That(Encoding.UTF8.GetString(nativeApi.Transfers[1].Buffer), Is.EqualTo("expected"));
                Assert.That(diagnostics.Snapshot(), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void HandleFetchDataAsync_PropagatesCancellationWithoutFailureTransfer()
        {
            CanceledContentProvider provider = new CanceledContentProvider();
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            byte[] content = Encoding.UTF8.GetBytes("cancel");
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            Assert.ThrowsAsync<OperationCanceledException>(() =>
                coordinator.HandleFetchDataAsync(request, new CancellationToken(canceled: true)));

            Assert.That(nativeApi.Transfers, Is.Empty);
        }

        [Test]
        public void HandleFetchDataAsync_DeletesTempFileWhenCanceled()
        {
            PartialCanceledContentProvider provider = new PartialCanceledContentProvider(Encoding.UTF8.GetBytes("partial"));
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            byte[] content = Encoding.UTF8.GetBytes("cancel");
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.HandleFetchDataAsync(request));

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers, Is.Empty);
                Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task HandleDehydrateAsync_AcknowledgesWithoutRemoteDownloadOrTransfer()
        {
            byte[] content = Encoding.UTF8.GetBytes("remote");
            FakeContentProvider provider = new FakeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            WindowsCloudFilesDehydrateRequest request = CreateDehydrateRequest(content);

            await coordinator.HandleDehydrateAsync(request);
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Is.Empty);
                Assert.That(nativeApi.Transfers, Is.Empty);
                Assert.That(nativeApi.Dehydrates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Dehydrates[0].RequestKey, Is.EqualTo(request.RequestKey));
                Assert.That(nativeApi.Dehydrates[0].FileIdentity, Is.EqualTo(request.FileIdentity));
                Assert.That(nativeApi.Dehydrates[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesAckDehydrateData.StatusSuccess));
                Assert.That(diagnostic.Operation, Is.EqualTo("dehydrate"));
                Assert.That(diagnostic.Status, Is.EqualTo("allowed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("remote-only.txt"));
            });
        }

        [Test]
        public async Task HandleDehydrateAsync_ReportsFailureWhenIdentityIsInvalid()
        {
            FakeContentProvider provider = new FakeContentProvider(Encoding.UTF8.GetBytes("remote"));
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            WindowsCloudFilesDehydrateRequest request = new WindowsCloudFilesDehydrateRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(2),
                new WindowsCloudFilesRequestKey(5),
                Encoding.UTF8.GetBytes("not-json"),
                @"\Device\HarddiskVolume1\Cotton\remote-only.txt",
                WindowsCloudFilesDehydrateReason.UserManual,
                IsBackground: false);

            await coordinator.HandleDehydrateAsync(request);
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Is.Empty);
                Assert.That(nativeApi.Transfers, Is.Empty);
                Assert.That(nativeApi.Dehydrates, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Dehydrates[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesAckDehydrateData.StatusUnsuccessful));
                Assert.That(diagnostic.Operation, Is.EqualTo("dehydrate"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo(request.NormalizedPath));
            });
        }

        [Test]
        public void NotifyDehydrateCompleted_RecordsCompletionDiagnostic()
        {
            byte[] content = Encoding.UTF8.GetBytes("remote");
            FakeContentProvider provider = new FakeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            WindowsCloudFilesDehydrateRequest request = CreateDehydrateRequest(content);

            coordinator.NotifyDehydrateCompleted(new WindowsCloudFilesDehydrateCompletionNotification(
                request.ConnectionKey,
                request.TransferKey,
                request.RequestKey,
                request.FileIdentity,
                request.NormalizedPath,
                WindowsCloudFilesDehydrateReason.UserManual,
                IsBackground: false,
                WasHydrated: true));
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Dehydrates, Is.Empty);
                Assert.That(diagnostic.Operation, Is.EqualTo("dehydrate"));
                Assert.That(diagnostic.Status, Is.EqualTo("completed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(diagnostic.Details, Does.Contain("UserManual"));
            });
        }

        private static WindowsCloudFilesFetchDataRequest CreateFetchRequest(
            byte[] content,
            long offset,
            long length,
            long requestKey = 3,
            string relativePath = "remote-only.txt",
            WindowsCloudFilesProcessInfo? processInfo = null)
        {
            RemoteFilePlaceholderRequest placeholder = CreatePlaceholderRequest(content, relativePath);
            string normalizedPath = SyncPath.Normalize(placeholder.RelativePath);
            byte[] identity = WindowsCloudFilesPlaceholderIdentity
                .Create(placeholder, normalizedPath)
                .ToBytes();

            return new WindowsCloudFilesFetchDataRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(2),
                new WindowsCloudFilesRequestKey(requestKey),
                identity,
                content.Length,
                offset,
                length,
                offset,
                length,
                @"\Device\HarddiskVolume1\Cotton\" + normalizedPath.Replace('/', '\\'),
                10,
                processInfo);
        }

        private static WindowsCloudFilesDehydrateRequest CreateDehydrateRequest(byte[] content)
        {
            RemoteFilePlaceholderRequest placeholder = CreatePlaceholderRequest(content);
            string normalizedPath = SyncPath.Normalize(placeholder.RelativePath);
            byte[] identity = WindowsCloudFilesPlaceholderIdentity
                .Create(placeholder, normalizedPath)
                .ToBytes();

            return new WindowsCloudFilesDehydrateRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(2),
                new WindowsCloudFilesRequestKey(4),
                identity,
                @"\Device\HarddiskVolume1\Cotton\remote-only.txt",
                WindowsCloudFilesDehydrateReason.UserManual,
                IsBackground: false);
        }

        private static RemoteFilePlaceholderRequest CreatePlaceholderRequest(
            byte[] content,
            string relativePath = "remote-only.txt")
        {
            return new RemoteFilePlaceholderRequest(
                "11111111-1111-1111-1111-111111111111",
                @"S:\CottonSync",
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = content.Length,
                    ContentHash = Convert.ToHexStringLower(SHA256.HashData(content)),
                    ETag = "etag",
                    CreatedAt = new DateTime(2026, 06, 16, 10, 00, 00, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 06, 16, 10, 05, 00, DateTimeKind.Utc),
                    Metadata = new Dictionary<string, string>(),
                });
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            }
        }

    }
}
