// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class WindowsCloudFilesCallbackDispatcherTests
    {
        [Test]
        public async Task QueueFetchData_RejectsRequestsWhenBoundedQueueIsFull()
        {
            var handler = new BlockingCallbackHandler();
            var transfers = new List<WindowsCloudFilesTransferData>();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                transfers.Add,
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 1));
            WindowsCloudFilesFetchDataRequest first = CreateRequest(1);
            WindowsCloudFilesFetchDataRequest second = CreateRequest(2);
            WindowsCloudFilesFetchDataRequest rejected = CreateRequest(3);

            Assert.That(dispatcher.QueueFetchData(first), Is.True);
            await handler.WaitForStartedCountAsync(1);
            Assert.That(dispatcher.QueueFetchData(second), Is.True);

            bool accepted = dispatcher.QueueFetchData(rejected);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(dispatcher.PendingFetchCount, Is.EqualTo(2));
                Assert.That(handler.StartedRequestKeys, Is.EqualTo(new[] { 1L }));
                Assert.That(transfers, Has.Count.EqualTo(1));
                Assert.That(transfers[0].RequestKey, Is.EqualTo(rejected.RequestKey));
                Assert.That(transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
            });
        }

        [Test]
        public async Task CancelFetchData_CancelsPendingRequestAndForwardsCancelCallback()
        {
            var handler = new BlockingCallbackHandler();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                _ => { },
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 4));
            WindowsCloudFilesFetchDataRequest fetch = CreateRequest(10);
            var cancel = new WindowsCloudFilesCancelFetchDataRequest(
                fetch.ConnectionKey,
                fetch.TransferKey,
                fetch.RequestKey,
                fetch.RequiredOffset,
                fetch.RequiredLength);

            Assert.That(dispatcher.QueueFetchData(fetch), Is.True);
            await handler.WaitForStartedCountAsync(1);

            dispatcher.CancelFetchData(cancel);

            await WaitUntilAsync(() => dispatcher.PendingFetchCount == 0);
            Assert.Multiple(() =>
            {
                Assert.That(handler.CancelRequests, Is.EqualTo(new[] { cancel }));
                Assert.That(handler.CanceledRequestKeys, Is.EqualTo(new[] { 10L }));
            });
        }

        [Test]
        public async Task QueueFetchData_TransfersFailureWhenHandlerThrows()
        {
            var handler = new ThrowingCallbackHandler(new InvalidOperationException("download failed"));
            var transfers = new List<WindowsCloudFilesTransferData>();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                transfers.Add,
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 4));
            WindowsCloudFilesFetchDataRequest request = CreateRequest(20);

            Assert.That(dispatcher.QueueFetchData(request), Is.True);

            await WaitUntilAsync(() => transfers.Count == 1);
            Assert.Multiple(() =>
            {
                Assert.That(transfers[0].RequestKey, Is.EqualTo(request.RequestKey));
                Assert.That(transfers[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesTransferData.StatusUnsuccessful));
                Assert.That(dispatcher.PendingFetchCount, Is.Zero);
            });
        }

        private static WindowsCloudFilesFetchDataRequest CreateRequest(long key)
        {
            return new WindowsCloudFilesFetchDataRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(key + 100),
                new WindowsCloudFilesRequestKey(key),
                [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                1024,
                0,
                1024,
                0,
                1024,
                @"\Device\HarddiskVolume1\Cotton\file.txt",
                8);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            }
        }

        private sealed class BlockingCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            private readonly object _gate = new();
            private readonly TaskCompletionSource _startedChanged =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public List<long> StartedRequestKeys { get; } = [];

            public List<long> CanceledRequestKeys { get; } = [];

            public List<WindowsCloudFilesCancelFetchDataRequest> CancelRequests { get; } = [];

            public async Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_gate)
                {
                    StartedRequestKeys.Add(request.RequestKey.Value);
                    _startedChanged.TrySetResult();
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        CanceledRequestKeys.Add(request.RequestKey.Value);
                    }

                    throw;
                }
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
                CancelRequests.Add(request);
            }

            public async Task WaitForStartedCountAsync(int count)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (StartedRequestKeys.Count >= count)
                        {
                            return;
                        }
                    }

                    await _startedChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
        }

        private sealed class ThrowingCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            private readonly Exception _exception;

            public ThrowingCallbackHandler(Exception exception)
            {
                _exception = exception;
            }

            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }
        }
    }
}
