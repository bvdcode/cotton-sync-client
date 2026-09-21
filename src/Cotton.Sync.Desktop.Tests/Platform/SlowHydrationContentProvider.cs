// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class SlowHydrationContentProvider(byte[] content) : IWindowsCloudFilesRemoteContentProvider
    {
        private const int ChunkCount = 14;
        private static readonly TimeSpan ChunkDelay = TimeSpan.FromSeconds(5);

        public async Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            int offset = 0;
            while (offset < content.Length)
            {
                await Task.Delay(ChunkDelay, cancellationToken);
                int length = Math.Min(content.Length / ChunkCount, content.Length - offset);
                await destination.WriteAsync(content.AsMemory(offset, length), cancellationToken);
                offset += length;
                transferProgress?.Report(new(SyncTransferDirection.Download, identity.RelativePath, offset, content.Length));
            }
        }
    }
}
