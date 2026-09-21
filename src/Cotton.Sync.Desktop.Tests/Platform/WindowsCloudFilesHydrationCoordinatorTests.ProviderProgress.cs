// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesHydrationCoordinatorTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task HandleFetchDataAsync_ReportsNativeProgressWithoutAnAppObserver(bool partialRange)
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789abcdef");
            FakeCloudFilesNativeApi nativeApi = new();
            IWindowsCloudFilesRemoteContentProvider provider = partialRange
                ? new VerifiedRangeContentProvider(content)
                : new ProgressContentProvider(content);
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, 0, partialRange ? 6 : content.Length);
            WindowsCloudFilesHydrationCoordinator coordinator = new(provider, nativeApi, _tempDirectory);

            await coordinator.HandleFetchDataAsync(request);

            long expectedTotal = partialRange ? 6 : content.Length;
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.ProviderProgress, Is.EqualTo(new[]
                {
                    new WindowsCloudFilesProviderProgress(request.ConnectionKey, request.TransferKey, expectedTotal, 0),
                    new WindowsCloudFilesProviderProgress(request.ConnectionKey, request.TransferKey, expectedTotal, expectedTotal),
                }));
                Assert.That(nativeApi.Transfers.Select(static item => item.CompletionStatus), Is.All.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
            });
        }

        [Test]
        public void ProviderProgressReporter_ThrottlesNativeCallsWhileForwardingEveryAppSample()
        {
            byte[] content = new byte[100];
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(content, 0, content.Length);
            FakeCloudFilesNativeApi nativeApi = new();
            RecordingProgress<SyncTransferProgress> appProgress = new();
            HydrationProgressTimeProvider clock = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(nativeApi, request, appProgress, CancellationToken.None, clock);

            reporter.Report(new(SyncTransferDirection.Download, "large.bin", 0, 100));
            clock.Advance(TimeSpan.FromMilliseconds(999));
            reporter.Report(new(SyncTransferDirection.Download, "large.bin", 10, 100));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            reporter.Report(new(SyncTransferDirection.Download, "large.bin", 20, 100));
            reporter.Report(new(SyncTransferDirection.Download, "large.bin", 100, 100, isCompleted: true));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.ProviderProgress.Select(static item => item.CompletedBytes), Is.EqualTo(new long[] { 0, 20, 100 }));
                Assert.That(appProgress.Values.Select(static item => item.TransferredBytes), Is.EqualTo(new long[] { 0, 10, 20, 100 }));
            });
        }

        [Test]
        public void ProviderProgressReporter_CancelledRequestDoesNotReportToNativeOrApp()
        {
            WindowsCloudFilesFetchDataRequest request = CreateFetchRequest(new byte[100], 0, 100);
            FakeCloudFilesNativeApi nativeApi = new();
            RecordingProgress<SyncTransferProgress> appProgress = new();
            using CancellationTokenSource cancellation = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(nativeApi, request, appProgress, cancellation.Token);
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => reporter.Report(new(SyncTransferDirection.Download, "large.bin", 10, 100)));
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.ProviderProgress, Is.Empty);
                Assert.That(appProgress.Values, Is.Empty);
            });
        }
    }
}
