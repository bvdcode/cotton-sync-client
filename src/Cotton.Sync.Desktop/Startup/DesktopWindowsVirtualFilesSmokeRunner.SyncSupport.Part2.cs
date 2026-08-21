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
    }
}
