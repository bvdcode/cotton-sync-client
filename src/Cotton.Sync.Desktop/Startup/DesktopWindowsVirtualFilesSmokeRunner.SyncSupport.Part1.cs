// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private class RecordingTransferProgress : IProgress<SyncTransferProgress>
        {
            private readonly object _gate = new();
            private readonly List<SyncTransferProgress> _values = [];

            public void Report(SyncTransferProgress value)
            {
                lock (_gate)
                {
                    _values.Add(value);
                }
            }

            public IReadOnlyList<SyncTransferProgress> Snapshot()
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }

            public void Clear()
            {
                lock (_gate)
                {
                    _values.Clear();
                }
            }

            public async Task<bool> WaitForSampleCountAsync(int count, TimeSpan timeout)
            {
                Stopwatch timer = Stopwatch.StartNew();
                while (timer.Elapsed < timeout)
                {
                    lock (_gate)
                    {
                        if (_values.Count >= count)
                        {
                            return true;
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
                }

                return false;
            }
        }

        private class RecordingRunProgressObserver : IObserver<AppRunProgress>
        {
            private readonly object _gate = new();
            private readonly List<AppRunProgress> _values = [];

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
                ArgumentNullException.ThrowIfNull(error);
            }

            public void OnNext(AppRunProgress value)
            {
                lock (_gate)
                {
                    _values.Add(value);
                }
            }

            public IReadOnlyList<AppRunProgress> Snapshot()
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }
        }

        private class ChunkedSmokeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;
            private readonly int _chunkSize;
            private TimeSpan _chunkDelay;

            public ChunkedSmokeContentProvider(byte[] content, int chunkSize, TimeSpan chunkDelay)
            {
                ArgumentNullException.ThrowIfNull(content);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
                _content = content;
                _chunkSize = chunkSize;
                _chunkDelay = chunkDelay;
            }

            public int DownloadCount { get; private set; }

            public int CancellationCount { get; private set; }

            public void ResetCancellation()
            {
                CancellationCount = 0;
            }

            public void SetChunkDelay(TimeSpan chunkDelay)
            {
                _chunkDelay = chunkDelay;
            }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(identity);
                ArgumentNullException.ThrowIfNull(destination);
                DownloadCount++;
                long transferred = 0;
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    0,
                    _content.LongLength,
                    isCompleted: false));

                try
                {
                    while (transferred < _content.LongLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int length = (int)Math.Min(_chunkSize, _content.LongLength - transferred);
                        await destination
                            .WriteAsync(_content.AsMemory((int)transferred, length), cancellationToken)
                            .ConfigureAwait(false);
                        transferred += length;
                        transferProgress?.Report(new SyncTransferProgress(
                            SyncTransferDirection.Download,
                            identity.RelativePath,
                            transferred,
                            _content.LongLength,
                            isCompleted: transferred == _content.LongLength));
                        if (_chunkDelay > TimeSpan.Zero && transferred < _content.LongLength)
                        {
                            await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    destination.Position = 0;
                }
                catch (OperationCanceledException)
                {
                    CancellationCount++;
                    throw;
                }
            }
        }

        private class RecordingCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            private readonly IWindowsCloudFilesCallbackHandler _inner;

            public RecordingCallbackHandler(IWindowsCloudFilesCallbackHandler inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public int CancelFetchDataCount { get; private set; }

            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return _inner.HandleFetchDataAsync(request, cancellationToken);
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
                CancelFetchDataCount++;
                _inner.CancelFetchData(request);
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return _inner.HandleDehydrateAsync(request, cancellationToken);
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
                _inner.NotifyDehydrateCompleted(notification);
            }
        }

        private class SinglePathRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly RemoteTreeSnapshot _tree;

            public SinglePathRemoteTreeCrawler(RemoteTreeSnapshot tree)
            {
                _tree = tree ?? throw new ArgumentNullException(nameof(tree));
            }

            public int FullCrawlCalls { get; private set; }

            public int PathLookupCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                FullCrawlCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_tree);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathLookupCalls++;
                ArgumentNullException.ThrowIfNull(relativePaths);
                cancellationToken.ThrowIfCancellationRequested();
                HashSet<string> requestedKeys = new(
                    relativePaths.Select(path => SyncPath.ToKey(SyncPath.Normalize(path))),
                    StringComparer.OrdinalIgnoreCase);
                RemoteTreeLookupSnapshot lookup = new()
                {
                    RootNode = _tree.RootNode,
                };

                foreach (RemoteDirectorySnapshot directory in _tree.Directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = SyncPath.ToKey(directory.RelativePath);
                    if (requestedKeys.Contains(key))
                    {
                        lookup.DirectoriesByPath[key] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in _tree.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = SyncPath.ToKey(file.RelativePath);
                    if (requestedKeys.Contains(key))
                    {
                        lookup.FilesByPath[key] = file;
                    }
                }

                progress?.Report(new RemoteTreeScanProgress(
                    lookup.FilesByPath.Count,
                    lookup.DirectoriesByPath.Count,
                    currentPath: null,
                    pagesScanned: 1));
                return Task.FromResult(lookup);
            }
        }

        private class RecordingUploadRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            public List<UploadCall> Uploads { get; } = [];

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedPath = SyncPath.Normalize(relativePath);
                string contentHash = string.IsNullOrWhiteSpace(localFile.ContentHash)
                    ? "missing-local-content-hash"
                    : localFile.ContentHash;
                NodeFileManifestDto returned = new()
                {
                    Id = existingRemoteFile?.Id ?? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    NodeId = existingRemoteFile?.NodeId ?? rootNodeId,
                    FileManifestId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    OriginalNodeFileId = existingRemoteFile?.OriginalNodeFileId == Guid.Empty
                        ? existingRemoteFile.Id
                        : existingRemoteFile?.OriginalNodeFileId ?? existingRemoteFile?.Id ?? Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = normalizedPath.Split('/')[^1],
                    ContentType = "application/octet-stream",
                    SizeBytes = localFile.SizeBytes,
                    ContentHash = contentHash,
                    ETag = "uploaded-" + contentHash,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = normalizedPath },
                };
                Uploads.Add(new UploadCall(rootNodeId, normalizedPath, localFile, existingRemoteFile, returned));
                return Task.FromResult(returned);
            }

            public Task DownloadFileAsync(
                Guid nodeFileId,
                Stream destination,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Cloud-only replacement smoke must not download remote content.");
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Cloud-only replacement smoke must not move remote files.");
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Cloud-only replacement smoke must not delete remote files.");
            }

            public record UploadCall(
                Guid RootNodeId,
                string RelativePath,
                LocalFileSnapshot LocalFile,
                NodeFileManifestDto? ExistingRemoteFile,
                NodeFileManifestDto Returned);
        }
    }
}
