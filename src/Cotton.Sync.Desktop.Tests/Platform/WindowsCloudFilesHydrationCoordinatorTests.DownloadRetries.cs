// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using Cotton.Sync.App.Runners;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesHydrationCoordinatorTests
    {
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        public async Task HandleFetchDataAsync_RetriesInterruptedBodyWithEmptyStream(bool partial, int failures)
        {
            byte[] content = "0123456789abcdef"u8.ToArray();
            InterruptedDownloadContentProvider provider = new(
                content, failures, new HttpIOException(HttpRequestError.ResponseEnded, "Truncated response."));
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator coordinator = CreateRetryCoordinator(provider, nativeApi, diagnostics);
            long offset = partial ? 4 : 0;
            long length = partial ? 6 : content.Length;

            await coordinator.HandleFetchDataAsync(CreateFetchRequest(content, offset, length));

            Assert.Multiple(() =>
            {
                Assert.That(provider.Requests, Has.Count.EqualTo(failures + 1));
                Assert.That(provider.Requests, Is.All.EqualTo((offset, length)));
                Assert.That(provider.InitialStreamStates, Is.All.EqualTo((0L, 0L)));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusSuccess));
                Assert.That(nativeApi.Transfers[0].Offset, Is.EqualTo(offset));
                Assert.That(nativeApi.Transfers[0].Buffer, Is.EqualTo(content.AsSpan((int)offset, (int)length).ToArray()));
                Assert.That(diagnostics.Snapshot().Count(item => item.Status == "retrying"), Is.EqualTo(failures));
                Assert.That(diagnostics.Snapshot().Any(item => item.Status == "failed"), Is.False);
                Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HandleFetchDataAsync_StopsAfterBoundedInterruptedBodyAttempts(bool partial)
        {
            byte[] content = "0123456789abcdef"u8.ToArray();
            InterruptedDownloadContentProvider provider = new(
                content, int.MaxValue, new HttpIOException(HttpRequestError.ResponseEnded, "Truncated response."));
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator coordinator = CreateRetryCoordinator(provider, nativeApi, diagnostics);

            await coordinator.HandleFetchDataAsync(CreateFetchRequest(content, 0, partial ? 6 : content.Length));

            Assert.Multiple(() =>
            {
                Assert.That(provider.Requests, Has.Count.EqualTo(3));
                Assert.That(provider.InitialStreamStates, Is.All.EqualTo((0L, 0L)));
                Assert.That(nativeApi.Transfers, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(nativeApi.Transfers[0].Buffer, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(diagnostics.Snapshot().Count(item => item.Status == "retrying"), Is.EqualTo(2));
                Assert.That(diagnostics.Snapshot().Count(item => item.Status == "failed"), Is.EqualTo(1));
                Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
            });
        }

        [Test]
        public async Task HandleFetchDataAsync_RejectsCorruptContentAfterSuccessfulRetry()
        {
            byte[] expected = "0123456789abcdef"u8.ToArray();
            InterruptedDownloadContentProvider provider = new(
                "fedcba9876543210"u8.ToArray(), 1,
                new HttpIOException(HttpRequestError.ResponseEnded, "Truncated response."));
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator coordinator = CreateRetryCoordinator(provider, nativeApi, diagnostics);

            await coordinator.HandleFetchDataAsync(CreateFetchRequest(expected, 0, expected.Length));

            Assert.Multiple(() =>
            {
                Assert.That(provider.Requests, Has.Count.EqualTo(2));
                Assert.That(nativeApi.Transfers.Single().CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(nativeApi.Transfers.Single().Buffer, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(diagnostics.Snapshot().Single(item => item.Status == "failed").Details, Does.Contain("hash"));
                Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HandleFetchDataAsync_DoesNotRetryLocalIoOrPermissionFailures(bool permissionDenied)
        {
            byte[] content = "0123456789abcdef"u8.ToArray();
            Exception failure = permissionDenied
                ? new HttpRequestException("Denied.", null, HttpStatusCode.Forbidden)
                : new IOException("Disk write failed.");
            InterruptedDownloadContentProvider provider = new(content, int.MaxValue, failure);
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator coordinator = CreateRetryCoordinator(provider, nativeApi, diagnostics);

            await coordinator.HandleFetchDataAsync(CreateFetchRequest(content, 0, content.Length));

            Assert.Multiple(() =>
            {
                Assert.That(provider.Requests, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers.Single().CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(diagnostics.Snapshot().Single().Status, Is.EqualTo("failed"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HandleFetchDataAsync_CancelDuringDownloadRetryStopsWithoutFailureTransfer(bool partial)
        {
            byte[] content = "0123456789abcdef"u8.ToArray();
            InterruptedDownloadContentProvider provider = new(
                content, int.MaxValue, new HttpIOException(HttpRequestError.ResponseEnded, "Truncated response."));
            FakeCloudFilesNativeApi nativeApi = new();
            WindowsCloudFilesDiagnostics diagnostics = new();
            WindowsCloudFilesHydrationCoordinator coordinator = CreateRetryCoordinator(
                provider, nativeApi, diagnostics, TimeSpan.FromSeconds(30));
            using CancellationTokenSource cancellation = new();
            Task hydration = coordinator.HandleFetchDataAsync(
                CreateFetchRequest(content, 0, partial ? 6 : content.Length), cancellation.Token);
            await WaitUntilAsync(() => diagnostics.Snapshot().Any(item => item.Status == "retrying"));

            cancellation.Cancel();

            Assert.That(async () => await hydration.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
            Assert.Multiple(() =>
            {
                Assert.That(provider.Requests, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Transfers, Is.Empty);
                Assert.That(Directory.GetFiles(_tempDirectory), Is.Empty);
            });
        }

        private WindowsCloudFilesHydrationCoordinator CreateRetryCoordinator(
            IWindowsCloudFilesRemoteContentProvider provider,
            FakeCloudFilesNativeApi nativeApi,
            WindowsCloudFilesDiagnostics diagnostics,
            TimeSpan? delay = null)
        {
            return new(provider, nativeApi, _tempDirectory, diagnostics,
                downloadRetryOptions: new SyncPairRunnerRetryOptions
                {
                    MaxAttempts = 3,
                    InitialDelay = delay ?? TimeSpan.Zero,
                    MaxDelay = delay ?? TimeSpan.Zero,
                });
        }
    }
}
