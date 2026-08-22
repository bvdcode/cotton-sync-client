// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {

        private class FakeRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly Queue<RemoteTreeSnapshot> _snapshots;
            private RemoteTreeSnapshot _lastSnapshot;

            public int CrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public FakeRemoteTreeCrawler(params RemoteTreeSnapshot[] snapshots)
            {
                if (snapshots.Length == 0)
                {
                    throw new ArgumentException("At least one remote snapshot is required.", nameof(snapshots));
                }

                _snapshots = new Queue<RemoteTreeSnapshot>(snapshots);
                _lastSnapshot = snapshots[0];
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                CrawlCalls++;
                return Task.FromResult(TakeNextSnapshot());
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                RemoteTreeSnapshot source = TakeNextSnapshot();
                RemoteTreeLookupSnapshot result = new()
                {
                    RootNode = source.RootNode,
                };
                foreach (RemoteDirectorySnapshot directory in source.Directories)
                {
                    if (relativePaths.Contains(directory.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        result.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in source.Files)
                {
                    if (relativePaths.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        result.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(result);
            }

            private RemoteTreeSnapshot TakeNextSnapshot()
            {
                if (_snapshots.Count > 0)
                {
                    _lastSnapshot = _snapshots.Dequeue();
                }

                return _lastSnapshot;
            }
        }


        private class FakeRemoteTreeProgressCrawler : IRemoteTreeProgressCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;
            private readonly IReadOnlyList<string> _progressPaths;

            public FakeRemoteTreeProgressCrawler(RemoteTreeSnapshot snapshot, params string[] progressPaths)
            {
                _snapshot = snapshot;
                _progressPaths = progressPaths.Length == 0
                    ? snapshot.Files.Select(file => file.RelativePath).ToList()
                    : progressPaths.ToList();
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(
                Guid rootNodeId,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                int entriesExpected = _progressPaths.Count + _snapshot.Directories.Count;
                progress?.Report(new RemoteTreeScanProgress(
                    0,
                    _snapshot.Directories.Count,
                    currentPath: null,
                    entriesExpected: entriesExpected));
                for (int index = 0; index < _progressPaths.Count; index++)
                {
                    progress?.Report(new RemoteTreeScanProgress(
                        index + 1,
                        _snapshot.Directories.Count,
                        _progressPaths[index],
                        entriesExpected: entriesExpected));
                }

                progress?.Report(new RemoteTreeScanProgress(
                    _progressPaths.Count,
                    _snapshot.Directories.Count,
                    currentPath: null,
                    entriesExpected: entriesExpected));
                return Task.FromResult(_snapshot);
            }
        }


        private class BlockingStreamingRemoteTreeCrawler : IRemoteTreeStreamingCrawler
        {
            private readonly Guid _rootNodeId;
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;
            private readonly RemoteTreeSnapshot? _snapshotCrawlResult;
            private readonly int? _entriesExpected;

            public BlockingStreamingRemoteTreeCrawler(
                Guid rootNodeId,
                IReadOnlyList<RemoteFileSnapshot> files,
                RemoteTreeSnapshot? snapshotCrawlResult = null,
                int? entriesExpected = null)
            {
                _rootNodeId = rootNodeId;
                _files = files;
                _snapshotCrawlResult = snapshotCrawlResult;
                _entriesExpected = entriesExpected;
            }

            public TaskCompletionSource FirstPlaceholderStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SnapshotCrawlCalls { get; private set; }

            public int StreamingCrawlCalls { get; private set; }

            public bool FirstPlaceholderStartedBeforeStreamingCompleted { get; private set; }

            public bool StreamingCompleted { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                if (_snapshotCrawlResult is not null)
                {
                    return Task.FromResult(_snapshotCrawlResult);
                }

                throw new InvalidOperationException("Initial virtual-files population must use streaming remote crawl.");
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                StreamingCrawlCalls++;
                NodeDto root = new NodeDto
                {
                    Id = _rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null));
                if (_entriesExpected.HasValue)
                {
                    progress?.Report(new RemoteTreeScanProgress(
                        0,
                        0,
                        currentPath: null,
                        pagesScanned: 1,
                        entriesExpected: _entriesExpected));
                }

                for (int index = 0; index < _files.Count; index++)
                {
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new RemoteTreeScanProgress(
                        index + 1,
                        0,
                        file.RelativePath,
                        pagesScanned: 1,
                        entriesExpected: _entriesExpected));
                    if (index == 0)
                    {
                        try
                        {
                            await FirstPlaceholderStarted.Task
                                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                                .ConfigureAwait(false);
                            FirstPlaceholderStartedBeforeStreamingCompleted = !StreamingCompleted;
                        }
                        catch (TimeoutException)
                        {
                            FirstPlaceholderStartedBeforeStreamingCompleted = false;
                        }
                    }
                }

                StreamingCompleted = true;
                progress?.Report(new RemoteTreeScanProgress(
                    _files.Count,
                    0,
                    currentPath: null,
                    pagesScanned: 1,
                    entriesExpected: _entriesExpected));
                return root;
            }
        }


        private class StreamingRemoteTreeCrawler : IRemoteTreeStreamingCrawler
        {
            private readonly Guid _rootNodeId;
            private readonly IReadOnlyList<RemoteFileSnapshot> _files;
            private readonly IReadOnlyList<RemoteDirectorySnapshot> _directories;

            public StreamingRemoteTreeCrawler(
                Guid rootNodeId,
                IReadOnlyList<RemoteFileSnapshot> files,
                IReadOnlyList<RemoteDirectorySnapshot>? directories = null)
            {
                _rootNodeId = rootNodeId;
                _files = files;
                _directories = directories ?? [];
            }

            public int SnapshotCrawlCalls { get; private set; }

            public int StreamingCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                throw new InvalidOperationException("Initial virtual-files population must use streaming remote crawl.");
            }

            public async Task<NodeDto> CrawlStreamingAsync(
                Guid rootNodeId,
                IRemoteTreeStreamSink sink,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                StreamingCrawlCalls++;
                NodeDto root = new NodeDto
                {
                    Id = _rootNodeId,
                    Name = "root",
                };
                progress?.Report(new RemoteTreeScanProgress(0, 0, currentPath: null));
                for (int index = 0; index < _directories.Count; index++)
                {
                    RemoteDirectorySnapshot directory = _directories[index];
                    await sink.AddDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new RemoteTreeScanProgress(
                        0,
                        index + 1,
                        directory.RelativePath,
                        pagesScanned: 1));
                }

                for (int index = 0; index < _files.Count; index++)
                {
                    RemoteFileSnapshot file = _files[index];
                    await sink.AddFileAsync(file, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new RemoteTreeScanProgress(
                        index + 1,
                        _directories.Count,
                        file.RelativePath,
                        pagesScanned: 1));
                }

                progress?.Report(new RemoteTreeScanProgress(
                    _files.Count,
                    _directories.Count,
                    currentPath: null,
                    pagesScanned: 1));
                return root;
            }
        }
    }
}
