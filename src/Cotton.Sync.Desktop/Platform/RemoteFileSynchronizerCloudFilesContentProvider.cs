// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class RemoteFileSynchronizerCloudFilesContentProvider : IWindowsCloudFilesRemoteContentProvider
    {
        private readonly IRemoteFileSynchronizer _remoteFiles;

        public RemoteFileSynchronizerCloudFilesContentProvider(IRemoteFileSynchronizer remoteFiles)
        {
            _remoteFiles = remoteFiles ?? throw new ArgumentNullException(nameof(remoteFiles));
        }

        public Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(destination);
            return _remoteFiles.DownloadFileAsync(identity.NodeFileId, destination, cancellationToken);
        }
    }
}
