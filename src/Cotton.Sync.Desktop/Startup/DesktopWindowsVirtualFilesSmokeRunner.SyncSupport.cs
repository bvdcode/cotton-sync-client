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
                var timer = Stopwatch.StartNew();
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
                var requestedKeys = new HashSet<string>(
                    relativePaths.Select(path => SyncPath.ToKey(SyncPath.Normalize(path))),
                    StringComparer.OrdinalIgnoreCase);
                var lookup = new RemoteTreeLookupSnapshot
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
                var returned = new NodeFileManifestDto
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

        private class RecordingRemoteDirectorySynchronizer : IRemoteDirectorySynchronizer
        {
            private readonly Dictionary<(Guid ParentNodeId, string Name), NodeDto> _children = [];

            public RecordingRemoteDirectorySynchronizer(Guid rootNodeId)
            {
                RootNodeId = rootNodeId;
            }

            public Guid RootNodeId { get; }

            public List<CreateDirectoryCall> Creates { get; } = [];

            public Task<NodeDto?> FindChildDirectoryAsync(
                Guid parentNodeId,
                string name,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _children.TryGetValue((parentNodeId, name), out NodeDto? node);
                return Task.FromResult(node);
            }

            public Task<NodeDto> CreateDirectoryAsync(
                Guid parentNodeId,
                string name,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_children.TryGetValue((parentNodeId, name), out NodeDto? existing))
                {
                    return Task.FromResult(existing);
                }

                var node = new NodeDto
                {
                    Id = Guid.CreateVersion7(),
                    ParentId = parentNodeId,
                    Name = name,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                _children[(parentNodeId, name)] = node;
                Creates.Add(new CreateDirectoryCall(parentNodeId, name, node));
                return Task.FromResult(node);
            }

            public Task DeleteDirectoryAsync(
                Guid nodeId,
                bool skipTrash = false,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Non-empty preservation smoke must not delete remote directories.");
            }

            public record CreateDirectoryCall(Guid ParentNodeId, string Name, NodeDto ReturnedNode);
        }

        private class GuardLocalScanner :
            ILocalFileScanner,
            ILocalTreeScanner,
            ILocalFileMetadataTreeScanner,
            ILocalFileMetadataTreeLookupScanner,
            ILocalFileMetadataPathLookupScanner,
            ILocalFileContentHasher,
            ILocalFilePresenceProbe
        {
            private readonly LocalFileScanner _scanner = new();

            public int FullScanCalls { get; private set; }

            public int MetadataTreeScanCalls { get; private set; }

            public int PathLookupCalls { get; private set; }

            public int PresenceProbeCalls { get; private set; }

            public Task<IReadOnlyList<LocalFileSnapshot>> ScanAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                FullScanCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not enumerate local placeholders.");
            }

            public Task<LocalTreeSnapshot> ScanTreeAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                FullScanCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not scan the local placeholder tree.");
            }

            public Task<LocalTreeSnapshot> ScanTreeMetadataAsync(
                string rootPath,
                CancellationToken cancellationToken = default)
            {
                MetadataTreeScanCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not scan local tree metadata.");
            }

            public Task<LocalTreeLookupSnapshot> ScanTreeMetadataLookupsAsync(
                string rootPath,
                IProgress<LocalTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                MetadataTreeScanCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not build local tree lookups.");
            }

            public Task<LocalTreeLookupSnapshot> ScanPathMetadataLookupsAsync(
                string rootPath,
                IReadOnlyCollection<string> relativePaths,
                IProgress<LocalTreeScanProgress>? progress,
                bool includeDirectoryDescendants,
                CancellationToken cancellationToken = default)
            {
                PathLookupCalls++;
                return _scanner.ScanPathMetadataLookupsAsync(
                    rootPath,
                    relativePaths,
                    progress,
                    includeDirectoryDescendants,
                    cancellationToken);
            }

            public bool FileExists(string rootPath, string relativePath)
            {
                PresenceProbeCalls++;
                return _scanner.FileExists(rootPath, relativePath);
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Steady-state repeat smoke must not hash local placeholder content.");
            }
        }

        private class GuardRemoteFilePlaceholderWriter :
            IRemoteFilePlaceholderWriter,
            IRemoteFilePlaceholderPopulationObserver
        {
            private int _beginPopulationCalls;
            private int _endPopulationCalls;
            private int _placeholderWriteCalls;

            public int BeginPopulationCalls => Volatile.Read(ref _beginPopulationCalls);

            public int EndPopulationCalls => Volatile.Read(ref _endPopulationCalls);

            public int PlaceholderWriteCalls => Volatile.Read(ref _placeholderWriteCalls);

            public IDisposable BeginPopulation(string syncPairId, string localRootPath)
            {
                Interlocked.Increment(ref _beginPopulationCalls);
                return new PopulationLease(this);
            }

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _placeholderWriteCalls);
                throw new InvalidOperationException(
                    "Steady-state repeat smoke must not create or refresh placeholders.");
            }

            private class PopulationLease : IDisposable
            {
                private GuardRemoteFilePlaceholderWriter? _owner;

                public PopulationLease(GuardRemoteFilePlaceholderWriter owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    GuardRemoteFilePlaceholderWriter? owner = Interlocked.Exchange(ref _owner, null);
                    if (owner is not null)
                    {
                        Interlocked.Increment(ref owner._endPopulationCalls);
                    }
                }
            }
        }

        private class LargeStateFirstRemoteCrawler : IRemoteTreeStreamingCrawler
        {
            private readonly Guid _rootNodeId;
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;

            public LargeStateFirstRemoteCrawler(Guid rootNodeId, IReadOnlyList<RemoteFileSnapshot> files)
            {
                _rootNodeId = rootNodeId;
                _files = files;
            }

            public int SnapshotCrawlCalls { get; private set; }

            public int StreamingCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must use streaming remote discovery.");
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                StreamingCrawlCalls++;
                var root = new NodeDto
                {
                    Id = _rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null, pagesScanned: 0));
                for (int index = 0; index < _files.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    if ((index + 1) % 1_000 == 0 || index == _files.Count - 1)
                    {
                        progress?.Report(new RemoteTreeScanProgress(
                            index + 1,
                            0,
                            file.RelativePath,
                            pagesScanned: (index / 1_000) + 1));
                    }
                }

                progress?.Report(new RemoteTreeScanProgress(
                    _files.Count,
                    0,
                    currentPath: null,
                    pagesScanned: Math.Max(1, (_files.Count + 999) / 1_000)));
                return root;
            }
        }

        private class InitialStreamingLoggingRemoteCrawler : IRemoteTreeStreamingCrawler
        {
            private readonly Guid _rootNodeId;
            private readonly RemoteDirectorySnapshot _directory;
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;

            public InitialStreamingLoggingRemoteCrawler(
                Guid rootNodeId,
                RemoteDirectorySnapshot directory,
                IReadOnlyList<RemoteFileSnapshot> files)
            {
                _rootNodeId = rootNodeId;
                _directory = directory;
                _files = files;
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Initial VFS logging smoke must use streaming remote discovery.");
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                NodeDto root = new()
                {
                    Id = _rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null, pagesScanned: 0));
                await sink.AddDirectoryAsync(_directory, cancellationToken).ConfigureAwait(false);
                progress?.Report(new RemoteTreeScanProgress(
                    0,
                    1,
                    _directory.RelativePath,
                    pagesScanned: 1,
                    pageReadLatencyTotal: TimeSpan.FromMilliseconds(3),
                    pageReadLatencyMax: TimeSpan.FromMilliseconds(3),
                    lastPageReadLatency: TimeSpan.FromMilliseconds(3)));

                for (int index = 0; index < _files.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    if ((index + 1) % 1_000 == 0 || index == _files.Count - 1)
                    {
                        int pagesScanned = (index / 1_000) + 2;
                        TimeSpan latency = TimeSpan.FromMilliseconds(4 + (pagesScanned % 7));
                        progress?.Report(new RemoteTreeScanProgress(
                            index + 1,
                            1,
                            file.RelativePath,
                            pagesScanned: pagesScanned,
                            pageReadLatencyTotal: TimeSpan.FromMilliseconds(3 + (pagesScanned * 5)),
                            pageReadLatencyMax: latency,
                            lastPageReadLatency: latency));
                    }
                }

                int totalPages = Math.Max(2, ((_files.Count + 999) / 1_000) + 1);
                progress?.Report(new RemoteTreeScanProgress(
                    _files.Count,
                    1,
                    currentPath: null,
                    pagesScanned: totalPages,
                    pageReadLatencyTotal: TimeSpan.FromMilliseconds(3 + (totalPages * 5)),
                    pageReadLatencyMax: TimeSpan.FromMilliseconds(10),
                    lastPageReadLatency: TimeSpan.FromMilliseconds(5)));
                return root;
            }
        }

        private class NoTransferRemoteFileSynchronizer : IRemoteFileSynchronizer
        {
            public int TransferCalls { get; private set; }

            public Task<NodeFileManifestDto> UploadFileAsync(
                Guid rootNodeId,
                string relativePath,
                LocalFileSnapshot localFile,
                NodeFileManifestDto? existingRemoteFile = null,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not upload files.");
            }

            public Task DownloadFileAsync(
                Guid nodeFileId,
                Stream destination,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not download files.");
            }

            public Task<NodeFileManifestDto> MoveFileAsync(
                Guid rootNodeId,
                string relativePath,
                NodeFileManifestDto existingRemoteFile,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not move remote files.");
            }

            public Task DeleteFileAsync(
                Guid nodeFileId,
                bool skipTrash = false,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
            {
                TransferCalls++;
                throw new InvalidOperationException("Steady-state repeat smoke must not delete remote files.");
            }
        }

        private class DelegateSyncPairWork : ISyncPairWork
        {
            private readonly Func<SyncPairSettings, SyncRunRequest, CancellationToken, Task> _run;

            public DelegateSyncPairWork(Func<SyncPairSettings, SyncRunRequest, CancellationToken, Task> run)
            {
                _run = run ?? throw new ArgumentNullException(nameof(run));
            }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return _run(syncPair, SyncRunRequest.Full, cancellationToken);
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return _run(syncPair, request, cancellationToken);
            }
        }

        private class NoopSyncPairWork : ISyncPairWork
        {
            public static NoopSyncPairWork Instance { get; } = new();

            private NoopSyncPairWork()
            {
            }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        private class FailOnInnerSyncPairWork : ISyncPairWork
        {
            private readonly string _message;

            public FailOnInnerSyncPairWork(string message)
            {
                _message = message;
            }

            public int RunCalls { get; private set; }

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                RunCalls++;
                throw new InvalidOperationException(_message);
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                RunCalls++;
                throw new InvalidOperationException(_message);
            }
        }
}
}
