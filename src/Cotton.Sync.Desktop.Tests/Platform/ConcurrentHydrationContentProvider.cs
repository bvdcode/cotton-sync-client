// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class ConcurrentHydrationContentProvider(byte[] content, bool waitForConcurrentDownloads) : IWindowsCloudFilesRemoteContentProvider
    {
        private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _downloads;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (Interlocked.Increment(ref _downloads) == 2)
            {
                _bothStarted.TrySetResult();
            }
            if (waitForConcurrentDownloads)
            {
                await _bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            await destination.WriteAsync(content, cancellationToken);
        }
    }
}
