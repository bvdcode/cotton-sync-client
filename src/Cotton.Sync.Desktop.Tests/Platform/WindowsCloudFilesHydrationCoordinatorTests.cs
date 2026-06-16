// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

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
        public async Task HandleFetchDataAsync_ReportsHydrationDownloadProgress()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            var provider = new ProgressContentProvider(content);
            var nativeApi = new FakeCloudFilesNativeApi();
            var progress = new RecordingProgress<SyncTransferProgress>();
            Guid expectedSyncPairId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var coordinator = new WindowsCloudFilesHydrationCoordinator(
                provider,
                nativeApi,
                _tempDirectory,
                transferProgressFactory: syncPairId =>
                {
                    Assert.That(syncPairId, Is.EqualTo(expectedSyncPairId));
                    return progress;
                });
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 4, length: 6);

            await coordinator.HandleFetchDataAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(progress.Values, Has.Count.EqualTo(3));
                Assert.That(progress.Values.Select(item => item.Direction), Is.All.EqualTo(SyncTransferDirection.Download));
                Assert.That(progress.Values.Select(item => item.RelativePath), Is.All.EqualTo("remote-only.txt"));
                Assert.That(progress.Values.Select(item => item.TotalBytes), Is.All.EqualTo(content.Length));
                Assert.That(progress.Values.Select(item => item.TransferredBytes), Is.EqualTo(new long[] { 0, 4, content.Length }));
                Assert.That(progress.Values.Last().IsCompleted, Is.True);
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
            });
        }

        [Test]
        public async Task RemoteContentProvider_UsesProgressAwareDownloadWhenAvailable()
        {
            var remoteFiles = new ProgressRemoteFileSynchronizer();
            var provider = new RemoteFileSynchronizerCloudFilesContentProvider(remoteFiles);
            var progress = new RecordingProgress<SyncTransferProgress>();
            byte[] content = Encoding.UTF8.GetBytes("remote");
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity
                .Create(CreatePlaceholderRequest(content), "remote-only.txt");
            await using var destination = new MemoryStream();

            await provider.DownloadAsync(identity, destination, progress);

            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.ProgressAwareDownloads, Is.EqualTo(1));
                Assert.That(remoteFiles.PlainDownloads, Is.Zero);
                Assert.That(remoteFiles.LastNodeFileId, Is.EqualTo(identity.NodeFileId));
                Assert.That(remoteFiles.LastRelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(remoteFiles.LastTotalBytes, Is.EqualTo(content.Length));
                Assert.That(remoteFiles.LastTransferProgress, Is.SameAs(progress));
            });
        }

        [Test]
        public void AppTransferProgressReporter_PublishesHydrationProgressToDesktopPipeline()
        {
            var publisher = new InMemoryAppTransferProgressPublisher();
            var observer = new RecordingObserver<AppTransferProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            Guid syncPairId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var reporter = new WindowsCloudFilesAppTransferProgressReporter(syncPairId, publisher);

            reporter.Report(new SyncTransferProgress(
                SyncTransferDirection.Download,
                "remote-only.txt",
                transferredBytes: 4,
                totalBytes: 16));

            AppTransferProgress appProgress = observer.Values.Single();
            Assert.Multiple(() =>
            {
                Assert.That(appProgress.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(appProgress.Direction, Is.EqualTo(SyncTransferDirection.Download));
                Assert.That(appProgress.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(appProgress.TransferredBytes, Is.EqualTo(4));
                Assert.That(appProgress.TotalBytes, Is.EqualTo(16));
                Assert.That(appProgress.IsCompleted, Is.False);
            });
        }

        [Test]
        public async Task QueueFetchData_StartsDeepTreeHydrationWithoutSyncTreeWork()
        {
            byte[] content = Encoding.UTF8.GetBytes("small");
            var provider = new BlockingStartContentProvider(content);
            var nativeApi = new FakeCloudFilesNativeApi();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
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
        public async Task HandleFetchDataAsync_AllowsRetryAfterFailedHydration()
        {
            byte[] expectedContent = Encoding.UTF8.GetBytes("expected");
            var provider = new SequencedContentProvider(
                Encoding.UTF8.GetBytes("mismatch"),
                expectedContent);
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
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
            var provider = new CanceledContentProvider();
            var nativeApi = new FakeCloudFilesNativeApi();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            byte[] content = Encoding.UTF8.GetBytes("cancel");
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            Assert.ThrowsAsync<OperationCanceledException>(() =>
                coordinator.HandleFetchDataAsync(request, new CancellationToken(canceled: true)));

            Assert.That(nativeApi.Transfers, Is.Empty);
        }

        [Test]
        public void HandleFetchDataAsync_DeletesTempFileWhenCanceled()
        {
            var provider = new PartialCanceledContentProvider(Encoding.UTF8.GetBytes("partial"));
            var nativeApi = new FakeCloudFilesNativeApi();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
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
            var provider = new FakeContentProvider(content);
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
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
            var provider = new FakeContentProvider(Encoding.UTF8.GetBytes("remote"));
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            var request = new WindowsCloudFilesDehydrateRequest(
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
            var provider = new FakeContentProvider(content);
            var nativeApi = new FakeCloudFilesNativeApi();
            var diagnostics = new WindowsCloudFilesDiagnostics();
            var coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
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
            string relativePath = "remote-only.txt")
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
                10);
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            }
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
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class ProgressContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public ProgressContentProvider(byte[] content)
            {
                _content = content;
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    transferredBytes: 0,
                    totalBytes: identity.SizeBytes));
                await destination.WriteAsync(_content.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    transferredBytes: 4,
                    totalBytes: identity.SizeBytes));
                await destination.WriteAsync(_content.AsMemory(4), cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    transferredBytes: _content.Length,
                    totalBytes: identity.SizeBytes,
                    isCompleted: true));
            }
        }

        private sealed class BlockingStartContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;
            private readonly TaskCompletionSource _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public BlockingStartContentProvider(byte[] content)
            {
                _content = content;
            }

            public TaskCompletionSource<WindowsCloudFilesPlaceholderIdentity> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Release()
            {
                _release.TrySetResult();
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                Started.TrySetResult(identity);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class SequencedContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly Queue<byte[]> _contents;

            public SequencedContentProvider(params byte[][] contents)
            {
                _contents = new Queue<byte[]>(contents);
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                byte[] content = _contents.Dequeue();
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class PartialCanceledContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public PartialCanceledContentProvider(byte[] content)
            {
                _content = content;
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private sealed class CanceledContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            public Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private sealed class ProgressRemoteFileSynchronizer : IRemoteFileTransferProgressSynchronizer
        {
            public int PlainDownloads { get; private set; }

            public int ProgressAwareDownloads { get; private set; }

            public Guid LastNodeFileId { get; private set; }

            public string LastRelativePath { get; private set; } = string.Empty;

            public long? LastTotalBytes { get; private set; }

            public IProgress<SyncTransferProgress>? LastTransferProgress { get; private set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile,
                IProgress<SyncTransferProgress>? transferProgress,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                PlainDownloads++;
                return Task.CompletedTask;
            }

            public Task DownloadFileAsync(
                Guid nodeFileId,
                string relativePath,
                long? totalBytes,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress,
                CancellationToken cancellationToken = default)
            {
                ProgressAwareDownloads++;
                LastNodeFileId = nodeFileId;
                LastRelativePath = relativePath;
                LastTotalBytes = totalBytes;
                LastTransferProgress = transferProgress;
                return Task.CompletedTask;
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class RecordingProgress<T> : IProgress<T>
        {
            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
            }
        }

        private sealed class RecordingObserver<T> : IObserver<T>
        {
            public List<T> Values { get; } = [];

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
                throw error;
            }

            public void OnNext(T value)
            {
                Values.Add(value);
            }
        }

        private sealed class FakeCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public List<WindowsCloudFilesAckDehydrateData> Dehydrates { get; } = [];

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
            }

            public void UnregisterSyncRoot(string localRootPath)
            {
                throw new NotSupportedException();
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
            }

            public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
            }

            public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
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

            public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
            {
                Dehydrates.Add(dehydrate with { FileIdentity = dehydrate.FileIdentity.ToArray() });
            }

            public void DehydratePlaceholder(string filePath)
            {
                throw new NotSupportedException();
            }
        }
    }
}
