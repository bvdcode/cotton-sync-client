// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    internal class VersionedHydrationContentProvider(IReadOnlyDictionary<string, byte[]> versions) : IWindowsCloudFilesRemoteContentProvider
    {
        public Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            byte[] content = versions[identity.ContentHash!];
            return destination.WriteAsync(content, cancellationToken).AsTask();
        }
    }
}
