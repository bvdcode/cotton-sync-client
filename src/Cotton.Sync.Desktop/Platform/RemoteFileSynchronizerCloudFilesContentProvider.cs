// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Desktop.Platform
{
    internal class RemoteFileSynchronizerCloudFilesContentProvider : IWindowsCloudFilesRemoteContentProvider
    {
        private readonly IRemoteFileSynchronizer _remoteFiles;

        public RemoteFileSynchronizerCloudFilesContentProvider(IRemoteFileSynchronizer remoteFiles)
        {
            _remoteFiles = remoteFiles ?? throw new ArgumentNullException(nameof(remoteFiles));
        }

        public Task DownloadAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(destination);
            if (_remoteFiles is IRemoteFileTransferProgressSynchronizer progressSynchronizer)
            {
                long? totalBytes = identity.SizeBytes < 0 ? null : identity.SizeBytes;
                return progressSynchronizer.DownloadFileAsync(
                    new RemoteFileDownloadIdentity(
                        identity.NodeFileId,
                        totalBytes,
                        identity.ETag ?? throw new InvalidDataException("Remote file has no content ETag.")),
                    identity.RelativePath,
                    destination,
                    transferProgress,
                    cancellationToken);
            }

            return _remoteFiles.DownloadFileAsync(identity.NodeFileId, destination, cancellationToken);
        }
    }

}
