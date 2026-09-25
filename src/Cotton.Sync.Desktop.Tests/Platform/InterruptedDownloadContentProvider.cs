// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class InterruptedDownloadContentProvider(
        byte[] content, int failures, Exception failure) : IWindowsCloudFilesRemoteContentProvider,
        IWindowsCloudFilesVerifiedRangeContentProvider
    {
        public List<(long Offset, long Length)> Requests { get; } = [];

        public List<(long Position, long Length)> InitialStreamStates { get; } = [];

        public Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            return DownloadVerifiedRangeAsync(identity, destination, 0, content.Length, transferProgress, cancellationToken);
        }

        public async Task DownloadVerifiedRangeAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            long offset,
            long length,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((offset, length));
            InitialStreamStates.Add((destination.Position, destination.Length));
            transferProgress?.Report(new(SyncTransferDirection.Download, identity.RelativePath, 0, length));
            bool interrupted = Requests.Count <= failures;
            int bytesToWrite = checked((int)(interrupted ? length / 2 : length));
            await destination.WriteAsync(content.AsMemory(checked((int)offset), bytesToWrite), cancellationToken);
            transferProgress?.Report(new(SyncTransferDirection.Download, identity.RelativePath, bytesToWrite, length));
            if (interrupted)
            {
                throw failure;
            }
        }
    }
}
