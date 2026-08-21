// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Desktop.Platform
{
    internal class RemoteFileRangeSynchronizerCloudFilesContentProvider :
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
