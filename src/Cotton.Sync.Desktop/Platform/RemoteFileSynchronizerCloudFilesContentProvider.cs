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
                    identity.NodeFileId,
                    identity.RelativePath,
                    totalBytes,
                    destination,
                    transferProgress,
                    cancellationToken);
            }

            return _remoteFiles.DownloadFileAsync(identity.NodeFileId, destination, cancellationToken);
        }
    }

    internal sealed class RemoteFileRangeSynchronizerCloudFilesContentProvider :
        RemoteFileSynchronizerCloudFilesContentProvider,
        IWindowsCloudFilesVerifiedRangeContentProvider
    {
        private readonly IRemoteFileRangeSynchronizer _remoteFiles;

        public RemoteFileRangeSynchronizerCloudFilesContentProvider(IRemoteFileRangeSynchronizer remoteFiles)
            : base(remoteFiles)
        {
            _remoteFiles = remoteFiles ?? throw new ArgumentNullException(nameof(remoteFiles));
        }

        public Task DownloadVerifiedRangeAsync(
            WindowsCloudFilesPlaceholderIdentity identity,
            Stream destination,
            long offset,
            long length,
            IProgress<SyncTransferProgress>? transferProgress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(destination);
            return _remoteFiles.DownloadFileRangeAsync(
                identity.NodeFileId,
                identity.RelativePath,
                offset,
                length,
                identity.ETag,
                destination,
                transferProgress,
                cancellationToken);
        }
    }
}
