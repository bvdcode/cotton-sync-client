// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

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
        public async Task CancelFetchData_DrainsRepeatedRequestsWithoutPendingTasks()
        {
            var handler = new BlockingCallbackHandler();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                _ => { },
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 4, QueueCapacity: 32));
            WindowsCloudFilesFetchDataRequest[] requests = Enumerable
                .Range(100, 20)
                .Select(key => CreateRequest(key))
                .ToArray();

            foreach (WindowsCloudFilesFetchDataRequest request in requests)
            {
                Assert.That(dispatcher.QueueFetchData(request), Is.True);
            }

            await handler.WaitForStartedCountAsync(4);

            foreach (WindowsCloudFilesFetchDataRequest request in requests)
            {
                dispatcher.CancelFetchData(
                    new WindowsCloudFilesCancelFetchDataRequest(
                        request.ConnectionKey,
                        request.TransferKey,
                        request.RequestKey,
                        request.RequiredOffset,
                        request.RequiredLength));
            }

            await WaitUntilAsync(() => dispatcher.PendingFetchCount == 0);
            Assert.Multiple(() =>
            {
                Assert.That(handler.CancelRequests, Has.Count.EqualTo(requests.Length));
                Assert.That(handler.CanceledRequestKeys, Has.Count.EqualTo(4));
                Assert.That(handler.CanceledRequestKeys, Is.EquivalentTo(handler.StartedRequestKeys));
            });
        }

        [Test]
        public async Task QueueFetchData_ReturnsWithoutWaitingForSlowHandler()
        {
            var handler = new BlockingCallbackHandler();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                _ => { },
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 4));
            WindowsCloudFilesFetchDataRequest first = CreateRequest(15);
            WindowsCloudFilesFetchDataRequest second = CreateRequest(16);

            Assert.That(dispatcher.QueueFetchData(first), Is.True);
            await handler.WaitForStartedCountAsync(1);

            Task<bool> enqueue = Task.Run(() => dispatcher.QueueFetchData(second));

            Assert.That(await enqueue.WaitAsync(TimeSpan.FromSeconds(1)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PendingFetchCount, Is.EqualTo(2));
                Assert.That(handler.StartedRequestKeys, Is.EqualTo(new[] { 15L }));
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

        [Test]
        public async Task QueueDehydrate_ReturnsWithoutWaitingForSlowHandler()
        {
            var handler = new BlockingCallbackHandler();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                _ => { },
                _ => { },
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 4));
            WindowsCloudFilesDehydrateRequest first = CreateDehydrateRequest(30);
            WindowsCloudFilesDehydrateRequest second = CreateDehydrateRequest(31);

            Assert.That(dispatcher.QueueDehydrate(first), Is.True);
            await handler.WaitForDehydrateStartedCountAsync(1);

            Task<bool> enqueue = Task.Run(() => dispatcher.QueueDehydrate(second));

            Assert.That(await enqueue.WaitAsync(TimeSpan.FromSeconds(1)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PendingDehydrateCount, Is.EqualTo(2));
                Assert.That(handler.StartedDehydrateRequestKeys, Is.EqualTo(new[] { 30L }));
            });
        }

        [Test]
        public async Task QueueDehydrate_RejectsRequestsWhenBoundedQueueIsFull()
        {
            var handler = new BlockingCallbackHandler();
            var acknowledgements = new List<WindowsCloudFilesAckDehydrateData>();
            using var dispatcher = new WindowsCloudFilesCallbackDispatcher(
                handler,
                _ => { },
                acknowledgements.Add,
                new WindowsCloudFilesCallbackDispatcherOptions(MaxConcurrentFetches: 1, QueueCapacity: 1));
            WindowsCloudFilesDehydrateRequest first = CreateDehydrateRequest(40);
            WindowsCloudFilesDehydrateRequest second = CreateDehydrateRequest(41);
            WindowsCloudFilesDehydrateRequest rejected = CreateDehydrateRequest(42);

            Assert.That(dispatcher.QueueDehydrate(first), Is.True);
            await handler.WaitForDehydrateStartedCountAsync(1);
            Assert.That(dispatcher.QueueDehydrate(second), Is.True);

            bool accepted = dispatcher.QueueDehydrate(rejected);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(dispatcher.PendingDehydrateCount, Is.EqualTo(2));
                Assert.That(acknowledgements, Has.Count.EqualTo(1));
                Assert.That(acknowledgements[0].RequestKey, Is.EqualTo(rejected.RequestKey));
                Assert.That(acknowledgements[0].CompletionStatus, Is.EqualTo(WindowsCloudFilesAckDehydrateData.StatusUnsuccessful));
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

        private static WindowsCloudFilesDehydrateRequest CreateDehydrateRequest(long key)
        {
            return new WindowsCloudFilesDehydrateRequest(
                new WindowsCloudFilesConnectionKey(1),
                new WindowsCloudFilesTransferKey(key + 100),
                new WindowsCloudFilesRequestKey(key),
                [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E],
                @"\Device\HarddiskVolume1\Cotton\file.txt",
                WindowsCloudFilesDehydrateReason.UserManual,
                IsBackground: false);
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
            private readonly SemaphoreSlim _started = new(0);
            private readonly SemaphoreSlim _dehydrateStarted = new(0);

            public List<long> StartedRequestKeys { get; } = [];

            public List<long> StartedDehydrateRequestKeys { get; } = [];

            public List<long> CanceledRequestKeys { get; } = [];

            public List<WindowsCloudFilesCancelFetchDataRequest> CancelRequests { get; } = [];

            public async Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_gate)
                {
                    StartedRequestKeys.Add(request.RequestKey.Value);
                    _started.Release();
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
                lock (_gate)
                {
                    CancelRequests.Add(request);
                }
            }

            public async Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_gate)
                {
                    StartedDehydrateRequestKeys.Add(request.RequestKey.Value);
                    _dehydrateStarted.Release();
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
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

                    await _started.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }

            public async Task WaitForDehydrateStartedCountAsync(int count)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (StartedDehydrateRequestKeys.Count >= count)
                        {
                            return;
                        }
                    }

                    await _dehydrateStarted.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }
    }
}
