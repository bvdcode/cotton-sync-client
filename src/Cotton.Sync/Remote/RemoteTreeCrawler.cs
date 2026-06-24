// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk.Nodes;
using Cotton.Sync;
using Cotton.Sync.State;
using System.Diagnostics;
using System.Threading.Channels;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Crawls remote Cotton folders through the SDK node API.
    /// </summary>
    public class RemoteTreeCrawler : IRemoteTreeLookupCrawler, IRemotePathLookupCrawler, IRemoteTreeStreamingCrawler
    {
        private const int DefaultPageSize = 500;
        private const int DefaultStreamingConcurrency = 8;
        private const int ProgressReportItemInterval = 100;
        private readonly ICottonNodeClient _nodes;
        private readonly int _pageSize;
        private readonly int _streamingConcurrency;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTreeCrawler" /> class.
        /// </summary>
        public RemoteTreeCrawler(
            ICottonNodeClient nodes,
            int pageSize = DefaultPageSize,
            int streamingConcurrency = DefaultStreamingConcurrency)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamingConcurrency);
            _nodes = nodes;
            _pageSize = pageSize;
            _streamingConcurrency = streamingConcurrency;
        }

        /// <inheritdoc />
        public async Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
        {
            return await CrawlAsync(rootNodeId, progress: null, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<RemoteTreeSnapshot> CrawlAsync(
            Guid rootNodeId,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            var snapshot = new RemoteTreeSnapshot();
            snapshot.RootNode = await CrawlCoreAsync(
                    rootNodeId,
                    progress,
                    snapshot.Directories.Add,
                    snapshot.Files.Add,
                    cancellationToken)
                .ConfigureAwait(false);
            snapshot.Directories.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
            snapshot.Files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
            return snapshot;
        }

        /// <inheritdoc />
        public async Task<RemoteTreeLookupSnapshot> CrawlLookupsAsync(
            Guid rootNodeId,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            var snapshot = new RemoteTreeLookupSnapshot();
            snapshot.RootNode = await CrawlCoreAsync(
                    rootNodeId,
                    progress,
                    directory => SyncPathLookup.Add(snapshot.DirectoriesByPath, directory, static item => item.RelativePath),
                    file => SyncPathLookup.Add(snapshot.FilesByPath, file, static item => item.RelativePath),
                    cancellationToken)
                .ConfigureAwait(false);
            return snapshot;
        }

        /// <inheritdoc />
        public async Task<NodeDto> CrawlStreamingAsync(
            Guid rootNodeId,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sink);
            return await CrawlStreamingCoreAsync(rootNodeId, sink, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
            Guid rootNodeId,
            IReadOnlyCollection<string> relativePaths,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            var snapshot = new RemoteTreeLookupSnapshot
            {
                RootNode = await _nodes.GetAsync(rootNodeId, cancellationToken).ConfigureAwait(false),
            };
            int directoriesScanned = 0;
            int filesScanned = 0;
            progress?.Report(new RemoteTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
            foreach (string relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                RemotePathResolution resolution = await ResolvePathAsync(
                        snapshot.RootNode,
                        normalizedPath,
                        directory =>
                        {
                            if (TryAddDirectory(snapshot, directory))
                            {
                                directoriesScanned++;
                                ReportDirectoryScanProgress(
                                    progress,
                                    filesScanned,
                                    directoriesScanned,
                                    RemoteTreePageReadMetrics.Empty,
                                    directory.RelativePath);
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolution.File is not null)
                {
                    if (TryAddFile(snapshot, resolution.File))
                    {
                        filesScanned++;
                        ReportScanProgress(
                            progress,
                            filesScanned,
                            directoriesScanned,
                            RemoteTreePageReadMetrics.Empty,
                            resolution.File.RelativePath);
                    }

                    continue;
                }

                if (resolution.Directory is not null)
                {
                    await CrawlCoreAsync(
                            resolution.Directory.Node.Id,
                            progress,
                            directory =>
                            {
                                if (TryAddDirectory(snapshot, directory))
                                {
                                    directoriesScanned++;
                                }
                            },
                            file =>
                            {
                                if (TryAddFile(snapshot, file))
                                {
                                    filesScanned++;
                                }
                            },
                            cancellationToken,
                            resolution.Directory.RelativePath)
                        .ConfigureAwait(false);
                }
            }

            progress?.Report(new RemoteTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
            return snapshot;
        }

        private async Task<NodeDto> CrawlCoreAsync(
            Guid rootNodeId,
            IProgress<RemoteTreeScanProgress>? progress,
            Action<RemoteDirectorySnapshot> addDirectory,
            Action<RemoteFileSnapshot> addFile,
            CancellationToken cancellationToken,
            string rootRelativePath = "")
        {
            ArgumentNullException.ThrowIfNull(addDirectory);
            ArgumentNullException.ThrowIfNull(addFile);
            NodeDto root = await _nodes.GetAsync(rootNodeId, cancellationToken).ConfigureAwait(false);
            var pending = new Stack<RemoteCrawlFrame>();
            pending.Push(new RemoteCrawlFrame(root, rootRelativePath, Page: 1, Loaded: 0));
            int directoriesScanned = 0;
            int filesScanned = 0;
            int pagesScanned = 0;
            int entriesExpected = 0;
            TimeSpan pageReadLatencyTotal = TimeSpan.Zero;
            TimeSpan pageReadLatencyMax = TimeSpan.Zero;
            TimeSpan lastPageReadLatency = TimeSpan.Zero;
            progress?.Report(new RemoteTreeScanProgress(
                filesScanned,
                directoriesScanned,
                currentPath: null,
                pagesScanned: pagesScanned,
                pageReadLatencyTotal: pageReadLatencyTotal,
                pageReadLatencyMax: pageReadLatencyMax,
                lastPageReadLatency: lastPageReadLatency,
                entriesExpected: entriesExpected));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RemoteCrawlFrame frame = pending.Pop();
                RemoteTreePageReadResult pageRead = await ReadChildrenPageAsync(frame, cancellationToken).ConfigureAwait(false);
                NodeContentDto children = pageRead.Children;
                pagesScanned++;
                lastPageReadLatency = pageRead.Elapsed;
                pageReadLatencyTotal += pageRead.Elapsed;
                pageReadLatencyMax = Max(pageReadLatencyMax, pageRead.Elapsed);
                RemoteTreePageReadMetrics pageMetrics = new(
                    pagesScanned,
                    pageReadLatencyTotal,
                    pageReadLatencyMax,
                    lastPageReadLatency);
                if (frame.Loaded == 0)
                {
                    entriesExpected += children.TotalCount;
                    progress?.Report(new RemoteTreeScanProgress(
                        filesScanned,
                        directoriesScanned,
                        currentPath: null,
                        pageMetrics.PagesScanned,
                        pageMetrics.PageReadLatencyTotal,
                        pageMetrics.PageReadLatencyMax,
                        pageMetrics.LastPageReadLatency,
                        entriesExpected: entriesExpected));
                }

                var childDirectories = new List<RemoteCrawlFrame>(children.Nodes.Count);
                foreach (NodeDto childNode in children.Nodes)
                {
                    string relativePath = Combine(frame.ParentPath, childNode.Name);
                    if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                    {
                        continue;
                    }

                    addDirectory(new RemoteDirectorySnapshot
                    {
                        RelativePath = relativePath,
                        Node = childNode,
                    });
                    directoriesScanned++;
                    ReportDirectoryScanProgress(
                        progress,
                        filesScanned,
                        directoriesScanned,
                        pageMetrics,
                        relativePath,
                        entriesExpected);
                    childDirectories.Add(new RemoteCrawlFrame(childNode, relativePath, Page: 1, Loaded: 0));
                }

                foreach (NodeFileManifestDto file in children.Files)
                {
                    string relativePath = Combine(frame.ParentPath, file.Name);
                    if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                    {
                        continue;
                    }

                    addFile(new RemoteFileSnapshot
                    {
                        RelativePath = relativePath,
                        File = file,
                    });
                    filesScanned++;
                    ReportScanProgress(
                        progress,
                        filesScanned,
                        directoriesScanned,
                        pageMetrics,
                        relativePath,
                        entriesExpected);
                }

                int count = children.Nodes.Count + children.Files.Count;
                int loaded = frame.Loaded + count;
                if (count != 0 && loaded < children.TotalCount)
                {
                    pending.Push(frame with { Page = frame.Page + 1, Loaded = loaded });
                }

                for (int index = childDirectories.Count - 1; index >= 0; index--)
                {
                    pending.Push(childDirectories[index]);
                }
            }

            progress?.Report(new RemoteTreeScanProgress(
                filesScanned,
                directoriesScanned,
                currentPath: null,
                pagesScanned: pagesScanned,
                pageReadLatencyTotal: pageReadLatencyTotal,
                pageReadLatencyMax: pageReadLatencyMax,
                lastPageReadLatency: lastPageReadLatency,
                entriesExpected: filesScanned + directoriesScanned));
            return root;
        }

        private async Task<NodeDto> CrawlStreamingCoreAsync(
            Guid rootNodeId,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken,
            string rootRelativePath = "")
        {
            NodeDto root = await _nodes.GetAsync(rootNodeId, cancellationToken).ConfigureAwait(false);
            var pending = Channel.CreateUnbounded<RemoteCrawlFrame>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false,
                });
            int pendingFrames = 0;
            int directoriesScanned = 0;
            int filesScanned = 0;
            int pagesScanned = 0;
            int entriesExpected = 0;
            long pageReadLatencyTotalTicks = 0;
            long pageReadLatencyMaxTicks = 0;
            long lastPageReadLatencyTicks = 0;
            progress?.Report(new RemoteTreeScanProgress(
                filesScanned,
                directoriesScanned,
                currentPath: null,
                pagesScanned: pagesScanned,
                entriesExpected: entriesExpected));

            async ValueTask EnqueueFrameAsync(RemoteCrawlFrame frame)
            {
                Interlocked.Increment(ref pendingFrames);
                try
                {
                    await pending.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    CompleteFrame();
                    throw;
                }
            }

            void CompleteFrame()
            {
                if (Interlocked.Decrement(ref pendingFrames) == 0)
                {
                    pending.Writer.TryComplete();
                }
            }

            await EnqueueFrameAsync(new RemoteCrawlFrame(root, rootRelativePath, Page: 1, Loaded: 0)).ConfigureAwait(false);

            Task[] workers = Enumerable
                .Range(0, _streamingConcurrency)
                .Select(_ => ConsumeStreamingFramesAsync(
                    pending,
                    sink,
                    progress,
                    () => Volatile.Read(ref filesScanned),
                    () => Volatile.Read(ref directoriesScanned),
                    () => Volatile.Read(ref pagesScanned),
                    () => Volatile.Read(ref entriesExpected),
                    () => Volatile.Read(ref pageReadLatencyTotalTicks),
                    () => Volatile.Read(ref pageReadLatencyMaxTicks),
                    () => Volatile.Read(ref lastPageReadLatencyTicks),
                    value => Interlocked.Add(ref filesScanned, value),
                    value => Interlocked.Add(ref directoriesScanned, value),
                    value => Interlocked.Add(ref pagesScanned, value),
                    value => Interlocked.Add(ref entriesExpected, value),
                    value => Interlocked.Add(ref pageReadLatencyTotalTicks, value),
                    value => UpdateMax(ref pageReadLatencyMaxTicks, value),
                    value => Interlocked.Exchange(ref lastPageReadLatencyTicks, value),
                    EnqueueFrameAsync,
                    CompleteFrame,
                    cancellationToken))
                .ToArray();

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                pending.Writer.TryComplete();
            }

            progress?.Report(new RemoteTreeScanProgress(
                Volatile.Read(ref filesScanned),
                Volatile.Read(ref directoriesScanned),
                currentPath: null,
                pagesScanned: Volatile.Read(ref pagesScanned),
                pageReadLatencyTotal: TimeSpan.FromTicks(Volatile.Read(ref pageReadLatencyTotalTicks)),
                pageReadLatencyMax: TimeSpan.FromTicks(Volatile.Read(ref pageReadLatencyMaxTicks)),
                lastPageReadLatency: TimeSpan.FromTicks(Volatile.Read(ref lastPageReadLatencyTicks)),
                entriesExpected: Volatile.Read(ref filesScanned) + Volatile.Read(ref directoriesScanned)));
            return root;
        }

        private async Task ConsumeStreamingFramesAsync(
            Channel<RemoteCrawlFrame> pending,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress>? progress,
            Func<int> getFilesScanned,
            Func<int> getDirectoriesScanned,
            Func<int> getPagesScanned,
            Func<int> getEntriesExpected,
            Func<long> getPageReadLatencyTotalTicks,
            Func<long> getPageReadLatencyMaxTicks,
            Func<long> getLastPageReadLatencyTicks,
            Func<int, int> addFilesScanned,
            Func<int, int> addDirectoriesScanned,
            Func<int, int> addPagesScanned,
            Func<int, int> addEntriesExpected,
            Func<long, long> addPageReadLatencyTicks,
            Func<long, long> updatePageReadLatencyMaxTicks,
            Func<long, long> setLastPageReadLatencyTicks,
            Func<RemoteCrawlFrame, ValueTask> enqueueFrameAsync,
            Action completeFrame,
            CancellationToken cancellationToken)
        {
            await foreach (RemoteCrawlFrame frame in pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RemoteTreePageReadResult pageRead = await ReadChildrenPageAsync(frame, cancellationToken).ConfigureAwait(false);
                    NodeContentDto children = pageRead.Children;
                    int pagesScanned = addPagesScanned(1);
                    long lastPageReadLatencyTicks = setLastPageReadLatencyTicks(pageRead.Elapsed.Ticks);
                    long pageReadLatencyTotalTicks = addPageReadLatencyTicks(pageRead.Elapsed.Ticks);
                    long pageReadLatencyMaxTicks = updatePageReadLatencyMaxTicks(pageRead.Elapsed.Ticks);
                    RemoteTreePageReadMetrics pageMetrics = new(
                        pagesScanned,
                        TimeSpan.FromTicks(pageReadLatencyTotalTicks),
                        TimeSpan.FromTicks(pageReadLatencyMaxTicks),
                        TimeSpan.FromTicks(lastPageReadLatencyTicks));
                    int entriesExpected = frame.Loaded == 0
                        ? addEntriesExpected(children.TotalCount)
                        : getEntriesExpected();
                    if (frame.Loaded == 0)
                    {
                        progress?.Report(new RemoteTreeScanProgress(
                            getFilesScanned(),
                            getDirectoriesScanned(),
                            currentPath: null,
                            pageMetrics.PagesScanned,
                            pageMetrics.PageReadLatencyTotal,
                            pageMetrics.PageReadLatencyMax,
                            pageMetrics.LastPageReadLatency,
                            entriesExpected: entriesExpected));
                    }

                    var childDirectories = new List<RemoteCrawlFrame>(children.Nodes.Count);
                    foreach (NodeDto childNode in children.Nodes)
                    {
                        string relativePath = Combine(frame.ParentPath, childNode.Name);
                        if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                        {
                            continue;
                        }

                        var directory = new RemoteDirectorySnapshot
                        {
                            RelativePath = relativePath,
                            Node = childNode,
                        };
                        await sink.AddDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                        int directoriesScanned = addDirectoriesScanned(1);
                        ReportDirectoryScanProgress(
                            progress,
                            getFilesScanned(),
                            directoriesScanned,
                            pageMetrics,
                            relativePath,
                            entriesExpected);
                        childDirectories.Add(new RemoteCrawlFrame(childNode, relativePath, Page: 1, Loaded: 0));
                    }

                    foreach (NodeFileManifestDto file in children.Files)
                    {
                        string relativePath = Combine(frame.ParentPath, file.Name);
                        if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                        {
                            continue;
                        }

                        await sink
                            .AddFileAsync(
                                new RemoteFileSnapshot
                                {
                                    RelativePath = relativePath,
                                    File = file,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        int filesScanned = addFilesScanned(1);
                        ReportScanProgress(
                            progress,
                            filesScanned,
                            getDirectoriesScanned(),
                            new RemoteTreePageReadMetrics(
                                getPagesScanned(),
                                TimeSpan.FromTicks(getPageReadLatencyTotalTicks()),
                                TimeSpan.FromTicks(getPageReadLatencyMaxTicks()),
                                TimeSpan.FromTicks(getLastPageReadLatencyTicks())),
                            relativePath,
                            getEntriesExpected());
                    }

                    int count = children.Nodes.Count + children.Files.Count;
                    int loaded = frame.Loaded + count;
                    if (count != 0 && loaded < children.TotalCount)
                    {
                        await enqueueFrameAsync(frame with { Page = frame.Page + 1, Loaded = loaded }).ConfigureAwait(false);
                    }

                    for (int index = childDirectories.Count - 1; index >= 0; index--)
                    {
                        await enqueueFrameAsync(childDirectories[index]).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    pending.Writer.TryComplete(exception);
                    throw;
                }
                finally
                {
                    completeFrame();
                }
            }
        }

        private async Task<RemotePathResolution> ResolvePathAsync(
            NodeDto root,
            string relativePath,
            Action<RemoteDirectorySnapshot> addDirectory,
            CancellationToken cancellationToken)
        {
            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            NodeDto currentNode = root;
            string currentPath = string.Empty;
            for (int index = 0; index < segments.Length; index++)
            {
                string segment = segments[index];
                NodeContentDto children = await FindChildPageContainingAsync(currentNode.Id, segment, cancellationToken).ConfigureAwait(false);
                NodeDto? childDirectory = children.Nodes.FirstOrDefault(node => string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
                bool isLast = index == segments.Length - 1;
                if (isLast)
                {
                    string childPath = string.IsNullOrEmpty(currentPath) ? segment : currentPath + "/" + segment;
                    if (childDirectory is not null)
                    {
                        var directory = new RemoteDirectorySnapshot
                        {
                            RelativePath = childPath,
                            Node = childDirectory,
                        };
                        addDirectory(directory);
                        return RemotePathResolution.ForDirectory(directory);
                    }

                    NodeFileManifestDto? file = children.Files.FirstOrDefault(item => string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
                    if (file is not null)
                    {
                        return RemotePathResolution.ForFile(new RemoteFileSnapshot
                        {
                            RelativePath = childPath,
                            File = file,
                        });
                    }

                    return RemotePathResolution.NotFound;
                }

                if (childDirectory is null)
                {
                    return RemotePathResolution.NotFound;
                }

                currentPath = string.IsNullOrEmpty(currentPath) ? segment : currentPath + "/" + segment;
                addDirectory(new RemoteDirectorySnapshot
                {
                    RelativePath = currentPath,
                    Node = childDirectory,
                });
                currentNode = childDirectory;
            }

            return RemotePathResolution.NotFound;
        }

        private async Task<NodeContentDto> FindChildPageContainingAsync(
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken)
        {
            int page = 1;
            int loaded = 0;
            while (true)
            {
                NodeContentDto children = await _nodes.GetChildrenAsync(
                    parentNodeId,
                    page,
                    _pageSize,
                    depth: 0,
                    cancellationToken).ConfigureAwait(false);
                if (children.Nodes.Any(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                    || children.Files.Any(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return children;
                }

                int count = children.Nodes.Count + children.Files.Count;
                loaded += count;
                if (count == 0 || loaded >= children.TotalCount)
                {
                    return children;
                }

                page++;
            }
        }

        private static bool TryAddDirectory(RemoteTreeLookupSnapshot snapshot, RemoteDirectorySnapshot directory)
        {
            return snapshot.DirectoriesByPath.TryAdd(SyncPath.ToKey(directory.RelativePath), directory);
        }

        private static bool TryAddFile(RemoteTreeLookupSnapshot snapshot, RemoteFileSnapshot file)
        {
            return snapshot.FilesByPath.TryAdd(SyncPath.ToKey(file.RelativePath), file);
        }

        private static void ReportScanProgress(
            IProgress<RemoteTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            RemoteTreePageReadMetrics pageMetrics,
            string currentPath,
            int? entriesExpected = null)
        {
            if (progress is null)
            {
                return;
            }

            if (filesScanned == 1 || filesScanned % ProgressReportItemInterval == 0)
            {
                progress.Report(new RemoteTreeScanProgress(
                    filesScanned,
                    directoriesScanned,
                    currentPath,
                    pageMetrics.PagesScanned,
                    pageMetrics.PageReadLatencyTotal,
                    pageMetrics.PageReadLatencyMax,
                    pageMetrics.LastPageReadLatency,
                    entriesExpected: entriesExpected));
            }
        }

        private static void ReportDirectoryScanProgress(
            IProgress<RemoteTreeScanProgress>? progress,
            int filesScanned,
            int directoriesScanned,
            RemoteTreePageReadMetrics pageMetrics,
            string currentPath,
            int? entriesExpected = null)
        {
            if (progress is null)
            {
                return;
            }

            if (directoriesScanned == 1 || directoriesScanned % ProgressReportItemInterval == 0)
            {
                progress.Report(new RemoteTreeScanProgress(
                    filesScanned,
                    directoriesScanned,
                    currentPath,
                    pageMetrics.PagesScanned,
                    pageMetrics.PageReadLatencyTotal,
                    pageMetrics.PageReadLatencyMax,
                    pageMetrics.LastPageReadLatency,
                    entriesExpected: entriesExpected));
            }
        }

        private async Task<RemoteTreePageReadResult> ReadChildrenPageAsync(
            RemoteCrawlFrame frame,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            NodeContentDto children = await _nodes.GetChildrenAsync(
                frame.Node.Id,
                frame.Page,
                _pageSize,
                depth: 0,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new RemoteTreePageReadResult(children, stopwatch.Elapsed);
        }

        private static TimeSpan Max(TimeSpan left, TimeSpan right)
        {
            return left >= right ? left : right;
        }

        private static long UpdateMax(ref long target, long value)
        {
            long current;
            do
            {
                current = Volatile.Read(ref target);
                if (value <= current)
                {
                    return current;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);

            return value;
        }

        private static string Combine(string parentPath, string name)
        {
            string combined = string.IsNullOrWhiteSpace(parentPath)
                ? name
                : parentPath + "/" + name;
            return SyncPath.Normalize(combined);
        }

        private readonly record struct RemoteCrawlFrame(NodeDto Node, string ParentPath, int Page, int Loaded);

        private readonly record struct RemoteTreePageReadMetrics(
            int PagesScanned,
            TimeSpan PageReadLatencyTotal,
            TimeSpan PageReadLatencyMax,
            TimeSpan LastPageReadLatency)
        {
            public static RemoteTreePageReadMetrics Empty { get; } = new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        }

        private readonly record struct RemoteTreePageReadResult(
            NodeContentDto Children,
            TimeSpan Elapsed);

        private sealed record RemotePathResolution(RemoteDirectorySnapshot? Directory, RemoteFileSnapshot? File)
        {
            public static RemotePathResolution NotFound { get; } = new(null, null);

            public static RemotePathResolution ForDirectory(RemoteDirectorySnapshot directory)
            {
                return new RemotePathResolution(directory, null);
            }

            public static RemotePathResolution ForFile(RemoteFileSnapshot file)
            {
                return new RemotePathResolution(null, file);
            }
        }
    }
}
