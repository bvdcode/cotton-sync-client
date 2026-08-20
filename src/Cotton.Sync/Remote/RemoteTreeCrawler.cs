// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk.Nodes;
using Cotton.Sync;
using Cotton.Sync.State;
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
        private readonly RemoteTreeDepthFirstCrawler _depthFirst;
        private readonly ICottonNodeClient _nodes;
        private readonly RemoteTreePageReader _pages;
        private readonly RemoteTreePathLookupCrawler _pathLookup;
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
            _streamingConcurrency = streamingConcurrency;
            _pages = new RemoteTreePageReader(nodes, pageSize);
            _depthFirst = new RemoteTreeDepthFirstCrawler(nodes, _pages);
            _pathLookup = new RemoteTreePathLookupCrawler(nodes, _pages, _depthFirst);
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
            RemoteTreeSnapshot snapshot = new RemoteTreeSnapshot();
            snapshot.RootNode = await _depthFirst.CrawlAsync(
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
            RemoteTreeLookupSnapshot snapshot = new RemoteTreeLookupSnapshot();
            snapshot.RootNode = await _depthFirst.CrawlAsync(
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
            return await _pathLookup.CrawlAsync(rootNodeId, relativePaths, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<NodeDto> CrawlStreamingCoreAsync(
            Guid rootNodeId,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken,
            string rootRelativePath = "")
        {
            NodeDto root = await _nodes.GetAsync(rootNodeId, cancellationToken).ConfigureAwait(false);
            Channel<RemoteCrawlFrame> pending = Channel.CreateUnbounded<RemoteCrawlFrame>(
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
                    RemoteTreePageReadResult pageRead = await _pages.ReadAsync(frame, cancellationToken).ConfigureAwait(false);
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
                        ? addEntriesExpected(pageRead.TotalCount)
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

                    List<RemoteCrawlFrame> childDirectories = new List<RemoteCrawlFrame>(children.Nodes.Count);
                    foreach (NodeDto childNode in children.Nodes)
                    {
                        string relativePath = RemoteTreePath.Combine(frame.ParentPath, childNode.Name);
                        if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                        {
                            continue;
                        }

                        RemoteDirectorySnapshot directory = new RemoteDirectorySnapshot
                        {
                            RelativePath = relativePath,
                            Node = childNode,
                        };
                        await sink.AddDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                        int directoriesScanned = addDirectoriesScanned(1);
                        RemoteTreeProgressReporter.ReportDirectory(
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
                        string relativePath = RemoteTreePath.Combine(frame.ParentPath, file.Name);
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
                        RemoteTreeProgressReporter.ReportFile(
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
                    if (count != 0 && loaded < pageRead.TotalCount)
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

    }
}
