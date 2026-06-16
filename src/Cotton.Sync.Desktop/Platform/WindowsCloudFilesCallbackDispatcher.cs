// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesCallbackDispatcher : IDisposable
    {
        private readonly IWindowsCloudFilesCallbackHandler _handler;
        private readonly Action<WindowsCloudFilesTransferData> _transferData;
        private readonly ConcurrentDictionary<long, PendingFetchData> _pendingFetches = [];
        private readonly Channel<PendingFetchData> _fetchQueue;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task[] _workers;
        private int _disposed;

        public WindowsCloudFilesCallbackDispatcher(
            IWindowsCloudFilesCallbackHandler handler,
            Action<WindowsCloudFilesTransferData> transferData,
            WindowsCloudFilesCallbackDispatcherOptions? options = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _transferData = transferData ?? throw new ArgumentNullException(nameof(transferData));
            WindowsCloudFilesCallbackDispatcherOptions normalized =
                (options ?? WindowsCloudFilesCallbackDispatcherOptions.Default).Normalize();
            _fetchQueue = Channel.CreateBounded<PendingFetchData>(
                new BoundedChannelOptions(normalized.QueueCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = normalized.MaxConcurrentFetches == 1,
                    SingleWriter = false,
                });
            _workers = Enumerable
                .Range(0, normalized.MaxConcurrentFetches)
                .Select(_ => Task.Run(RunFetchWorkerAsync))
                .ToArray();
        }

        public int PendingFetchCount => _pendingFetches.Count;

        public bool QueueFetchData(WindowsCloudFilesFetchDataRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (_disposed != 0)
            {
                return false;
            }

            var pending = new PendingFetchData(
                request,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            if (!_pendingFetches.TryAdd(request.RequestKey.Value, pending))
            {
                pending.Dispose();
                TryTransferFailure(request);
                return false;
            }

            if (_fetchQueue.Writer.TryWrite(pending))
            {
                return true;
            }

            if (_pendingFetches.TryRemove(request.RequestKey.Value, out PendingFetchData? rejected))
            {
                rejected.Dispose();
            }

            TryTransferFailure(request);
            return false;
        }

        public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (_pendingFetches.TryGetValue(request.RequestKey.Value, out PendingFetchData? pending))
            {
                pending.Cancel();
            }

            _handler.CancelFetchData(request);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _fetchQueue.Writer.TryComplete();
            _lifetime.Cancel();
            foreach (PendingFetchData pending in _pendingFetches.Values)
            {
                pending.Cancel();
            }
        }

        private async Task RunFetchWorkerAsync()
        {
            try
            {
                await foreach (PendingFetchData pending in _fetchQueue.Reader
                                   .ReadAllAsync(_lifetime.Token)
                                   .ConfigureAwait(false))
                {
                    await ProcessFetchDataAsync(pending).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private async Task ProcessFetchDataAsync(PendingFetchData pending)
        {
            try
            {
                if (!pending.IsCancellationRequested)
                {
                    await _handler
                        .HandleFetchDataAsync(pending.Request, pending.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested)
            {
            }
            catch
            {
                TryTransferFailure(pending.Request);
            }
            finally
            {
                if (_pendingFetches.TryRemove(pending.Request.RequestKey.Value, out PendingFetchData? removed))
                {
                    removed.Dispose();
                }
            }
        }

        private void TryTransferFailure(WindowsCloudFilesFetchDataRequest request)
        {
            try
            {
                _transferData(WindowsCloudFilesTransferData.Failure(request));
            }
            catch
            {
            }
        }

        private sealed class PendingFetchData : IDisposable
        {
            private readonly CancellationTokenSource _cancellation;

            public PendingFetchData(
                WindowsCloudFilesFetchDataRequest request,
                CancellationTokenSource cancellation)
            {
                Request = request;
                _cancellation = cancellation;
            }

            public WindowsCloudFilesFetchDataRequest Request { get; }

            public CancellationToken Token => _cancellation.Token;

            public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

            public void Cancel()
            {
                _cancellation.Cancel();
            }

            public void Dispose()
            {
                _cancellation.Dispose();
            }
        }
    }
}
