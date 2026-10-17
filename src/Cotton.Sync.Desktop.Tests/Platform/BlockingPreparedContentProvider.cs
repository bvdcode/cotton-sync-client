// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class BlockingPreparedContentProvider(TaskCompletionSource ready) : IWindowsCloudFilesRemoteContentProvider
    {
        public async Task DownloadAsync(WindowsCloudFilesPlaceholderIdentity identity, Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null, CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            ready.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
