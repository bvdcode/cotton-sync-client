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
    [Platform(Include = "Win")]
    public partial class WindowsCloudFilesHydrationCoordinatorTests
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
            FakeContentProvider provider = new FakeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
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
        public async Task HandleFetchDataAsync_UsesVerifiedRangeProviderForPartialRequest()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            VerifiedRangeContentProvider provider = new VerifiedRangeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 4, length: 6);

            await coordinator.HandleFetchDataAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Is.Empty);
                Assert.That(provider.RangeDownloads, Has.Count.EqualTo(1));
                Assert.That(provider.RangeDownloads[0].Identity.NodeFileId, Is.EqualTo(Guid.Parse("33333333-3333-3333-3333-333333333333")));
                Assert.That(provider.RangeDownloads[0].Offset, Is.EqualTo(4));
                Assert.That(provider.RangeDownloads[0].Length, Is.EqualTo(6));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(nativeApi.Transfers[0].Offset, Is.EqualTo(4));
                Assert.That(nativeApi.Transfers[0].Length, Is.EqualTo(6));
                Assert.That(Encoding.UTF8.GetString(nativeApi.Transfers[0].Buffer), Is.EqualTo("456789"));
                Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_UsesFullDownloadForFullRequestWhenRangeProviderExists()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            VerifiedRangeContentProvider provider = new VerifiedRangeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            await coordinator.HandleFetchDataAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Has.Count.EqualTo(1));
                Assert.That(provider.RangeDownloads, Is.Empty);
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(Encoding.UTF8.GetString(nativeApi.Transfers[0].Buffer), Is.EqualTo("0123456789abcdef"));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { request.NormalizedPath }));
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_ReportsFailureWhenVerifiedRangeSizeDoesNotMatch()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            VerifiedRangeContentProvider provider = new VerifiedRangeContentProvider(content, rangeBytesToWrite: 3);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory, diagnostics);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 4, length: 6);

            await coordinator.HandleFetchDataAsync(request);
            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();

            Assert.Multiple(() =>
            {
                Assert.That(provider.DownloadedIdentities, Is.Empty);
                Assert.That(provider.RangeDownloads, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(diagnostic.Operation, Is.EqualTo("hydrate"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.Details, Does.Contain("range size"));
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_MarksFullHydrationInSync()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            FakeContentProvider provider = new FakeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(provider, nativeApi, _tempDirectory);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            await coordinator.HandleFetchDataAsync(request);

            Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { request.NormalizedPath }));
        }

        [Test]
        public async Task HandleFetchDataAsync_RecordsFailureWhenFullHydrationDoesNotReportInSync()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            FakeContentProvider provider = new FakeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                InSyncStateAfterSet = WindowsCloudFilesPlaceholderState.Placeholder,
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(
                provider,
                nativeApi,
                _tempDirectory,
                diagnostics);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, offset: 0, length: content.Length);

            await coordinator.HandleFetchDataAsync(request);

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { request.NormalizedPath }));
                Assert.That(diagnostic.Operation, Is.EqualTo("hydrate-in-sync"));
                Assert.That(diagnostic.Status, Is.EqualTo("failed"));
                Assert.That(diagnostic.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(diagnostic.Details, Does.Contain("did not report in-sync state"));
                Assert.That(diagnostic.Details, Does.Contain("Placeholder"));
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_ReportsHydrationDownloadProgress()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            ProgressContentProvider provider = new ProgressContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();
            Guid expectedSyncPairId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(
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
        public async Task HandleFetchDataAsync_RecordsRequesterProcessInfoWhenAvailable()
        {
            byte[] content = Encoding.UTF8.GetBytes("hello world");
            FakeContentProvider provider = new FakeContentProvider(content);
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesHydrationCoordinator coordinator = new WindowsCloudFilesHydrationCoordinator(
                provider,
                nativeApi,
                _tempDirectory,
                diagnostics);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(
                content,
                offset: 0,
                length: content.Length,
                processInfo: new WindowsCloudFilesProcessInfo(
                    1234,
                    @"\Device\HarddiskVolume3\Windows\explorer.exe",
                    null,
                    null,
                    @"C:\Windows\explorer.exe",
                    1));

            await coordinator.HandleFetchDataAsync(request);

            WindowsCloudFilesDiagnosticEvent diagnostic = diagnostics
                .Snapshot()
                .Single(static item => item.Operation == "hydrate" && item.Status == "requested");
            Assert.Multiple(() =>
            {
                Assert.That(diagnostic.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(diagnostic.Details, Does.Contain("pid=1234"));
                Assert.That(diagnostic.Details, Does.Contain("session=1"));
                Assert.That(diagnostic.Details, Does.Contain(@"\Device\HarddiskVolume3\Windows\explorer.exe"));
                Assert.That(diagnostic.Details, Does.Contain("requiredLength=11"));
            });
        }

        [Test]
        public async Task RemoteContentProvider_UsesProgressAwareDownloadWhenAvailable()
        {
            ProgressRemoteFileSynchronizer remoteFiles = new ProgressRemoteFileSynchronizer();
            RemoteFileSynchronizerCloudFilesContentProvider provider = new RemoteFileSynchronizerCloudFilesContentProvider(remoteFiles);
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();
            byte[] content = Encoding.UTF8.GetBytes("remote");
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity
                .Create(CreatePlaceholderRequest(content), "remote-only.txt");
            await using MemoryStream destination = new MemoryStream();

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
        public async Task RemoteRangeContentProvider_UsesRangeDownloadWithPlaceholderETag()
        {
            ProgressRemoteFileSynchronizer remoteFiles = new ProgressRemoteFileSynchronizer();
            RemoteFileRangeSynchronizerCloudFilesContentProvider provider = new RemoteFileRangeSynchronizerCloudFilesContentProvider(remoteFiles);
            RecordingProgress<SyncTransferProgress> progress = new RecordingProgress<SyncTransferProgress>();
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity
                .Create(CreatePlaceholderRequest(content), "remote-only.txt");
            await using MemoryStream destination = new MemoryStream();

            await provider.DownloadVerifiedRangeAsync(
                identity,
                destination,
                offset: 4,
                length: 6,
                transferProgress: progress);

            Assert.Multiple(() =>
            {
                Assert.That(remoteFiles.RangeDownloads, Is.EqualTo(1));
                Assert.That(remoteFiles.ProgressAwareDownloads, Is.Zero);
                Assert.That(remoteFiles.PlainDownloads, Is.Zero);
                Assert.That(remoteFiles.LastNodeFileId, Is.EqualTo(identity.NodeFileId));
                Assert.That(remoteFiles.LastRelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(remoteFiles.LastOffset, Is.EqualTo(4));
                Assert.That(remoteFiles.LastLength, Is.EqualTo(6));
                Assert.That(remoteFiles.LastExpectedETag, Is.EqualTo("etag"));
                Assert.That(remoteFiles.LastTransferProgress, Is.SameAs(progress));
            });
        }

        [Test]
        public void AppTransferProgressReporter_PublishesHydrationProgressToDesktopPipeline()
        {
            InMemoryAppTransferProgressPublisher publisher = new InMemoryAppTransferProgressPublisher();
            RecordingObserver<AppTransferProgress> observer = new RecordingObserver<AppTransferProgress>();
            using IDisposable subscription = publisher.Subscribe(observer);
            Guid syncPairId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            WindowsCloudFilesAppTransferProgressReporter reporter = new WindowsCloudFilesAppTransferProgressReporter(syncPairId, publisher);

            reporter.Report(new SyncTransferProgress(
                SyncTransferDirection.Download,
                "remote-only.txt",
                transferredBytes: 4,
                totalBytes: 16));
            reporter.Complete();

            AppTransferProgress appProgress = observer.Values[0];
            Assert.Multiple(() =>
            {
                Assert.That(observer.Values, Has.Count.EqualTo(2));
                Assert.That(appProgress.SyncPairId, Is.EqualTo(syncPairId));
                Assert.That(appProgress.Direction, Is.EqualTo(SyncTransferDirection.Download));
                Assert.That(appProgress.RelativePath, Is.EqualTo("remote-only.txt"));
                Assert.That(appProgress.TransferredBytes, Is.EqualTo(4));
                Assert.That(appProgress.TotalBytes, Is.EqualTo(16));
                Assert.That(appProgress.IsCompleted, Is.False);
                Assert.That(observer.Values[1].IsCompleted, Is.True);
            });
        }

    }
}
