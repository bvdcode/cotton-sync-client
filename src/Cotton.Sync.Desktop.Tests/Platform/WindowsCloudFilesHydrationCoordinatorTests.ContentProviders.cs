// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.Progress;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesHydrationCoordinatorTests
    {
        private class FakeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public FakeContentProvider(byte[] content)
            {
                _content = content;
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
            }
        }

        private class VerifiedRangeContentProvider :
            IWindowsCloudFilesRemoteContentProvider,
            IWindowsCloudFilesVerifiedRangeContentProvider
        {
            private readonly byte[] _content;
            private readonly int? _rangeBytesToWrite;

            public VerifiedRangeContentProvider(byte[] content, int? rangeBytesToWrite = null)
            {
                _content = content;
                _rangeBytesToWrite = rangeBytesToWrite;
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public List<(WindowsCloudFilesPlaceholderIdentity Identity, long Offset, long Length)> RangeDownloads { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
            }

            public async Task DownloadVerifiedRangeAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                long offset,
                long length,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                RangeDownloads.Add((identity, offset, length));
                int bytesToWrite = _rangeBytesToWrite ?? checked((int)length);
                await destination
                    .WriteAsync(_content.AsMemory(checked((int)offset), bytesToWrite), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private class ProgressContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public ProgressContentProvider(byte[] content)
            {
                _content = content;
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    transferredBytes: 0,
                    totalBytes: identity.SizeBytes));
                await destination.WriteAsync(_content.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    transferredBytes: 4,
                    totalBytes: identity.SizeBytes));
                await destination.WriteAsync(_content.AsMemory(4), cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    transferredBytes: _content.Length,
                    totalBytes: identity.SizeBytes,
                    isCompleted: true));
            }
        }

        private class BlockingStartContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;
            private readonly TaskCompletionSource _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public BlockingStartContentProvider(byte[] content)
            {
                _content = content;
            }

            public TaskCompletionSource<WindowsCloudFilesPlaceholderIdentity> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Release()
            {
                _release.TrySetResult();
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                Started.TrySetResult(identity);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
            }
        }

        private class SequencedContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly Queue<byte[]> _contents;

            public SequencedContentProvider(params byte[][] contents)
            {
                _contents = new Queue<byte[]>(contents);
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                byte[] content = _contents.Dequeue();
                await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
        }

        private class PartialCanceledContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public PartialCanceledContentProvider(byte[] content)
            {
                _content = content;
            }

            public List<WindowsCloudFilesPlaceholderIdentity> DownloadedIdentities { get; } = [];

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                DownloadedIdentities.Add(identity);
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private class CanceledContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            public Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private class ProgressRemoteFileSynchronizer :
            IRemoteFileTransferProgressSynchronizer,
            IRemoteFileRangeSynchronizer
        {
            public int PlainDownloads { get; private set; }

            public int ProgressAwareDownloads { get; private set; }

            public int RangeDownloads { get; private set; }

            public Guid LastNodeFileId { get; private set; }

            public string LastRelativePath { get; private set; } = string.Empty;

            public long? LastTotalBytes { get; private set; }

            public long LastOffset { get; private set; }

            public long LastLength { get; private set; }

            public string? LastExpectedETag { get; private set; }

            public IProgress<SyncTransferProgress>? LastTransferProgress { get; private set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile,
                IProgress<SyncTransferProgress>? transferProgress,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                PlainDownloads++;
                return Task.CompletedTask;
            }

            public Task DownloadFileAsync(
                Guid nodeFileId,
                string relativePath,
                long? totalBytes,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress,
                CancellationToken cancellationToken = default)
            {
                ProgressAwareDownloads++;
                LastNodeFileId = nodeFileId;
                LastRelativePath = relativePath;
                LastTotalBytes = totalBytes;
                LastTransferProgress = transferProgress;
                return Task.CompletedTask;
            }

            public Task DownloadFileRangeAsync(
                Guid nodeFileId,
                string relativePath,
                long offset,
                long length,
                string? expectedETag,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress,
                CancellationToken cancellationToken = default)
            {
                RangeDownloads++;
                LastNodeFileId = nodeFileId;
                LastRelativePath = relativePath;
                LastOffset = offset;
                LastLength = length;
                LastExpectedETag = expectedETag;
                LastTransferProgress = transferProgress;
                return Task.CompletedTask;
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

    }
}
