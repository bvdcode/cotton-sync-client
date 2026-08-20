// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk.Nodes;
using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    internal class RemoteTreeDepthFirstCrawler
    {
        private readonly ICottonNodeClient _nodes;
        private readonly RemoteTreePageReader _pages;

        public RemoteTreeDepthFirstCrawler(ICottonNodeClient nodes, RemoteTreePageReader pages)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        }

        public async Task<NodeDto> CrawlAsync(
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
            Stack<RemoteCrawlFrame> pending = new Stack<RemoteCrawlFrame>();
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
                RemoteTreePageReadResult pageRead = await _pages.ReadAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
                NodeContentDto children = pageRead.Children;
                pagesScanned++;
                lastPageReadLatency = pageRead.Elapsed;
                pageReadLatencyTotal += pageRead.Elapsed;
                pageReadLatencyMax = pageReadLatencyMax >= pageRead.Elapsed
                    ? pageReadLatencyMax
                    : pageRead.Elapsed;
                RemoteTreePageReadMetrics pageMetrics = new RemoteTreePageReadMetrics(
                    pagesScanned,
                    pageReadLatencyTotal,
                    pageReadLatencyMax,
                    lastPageReadLatency);
                if (frame.Loaded == 0)
                {
                    entriesExpected += pageRead.TotalCount;
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

                List<RemoteCrawlFrame> childDirectories = new List<RemoteCrawlFrame>(children.Nodes.Count);
                foreach (NodeDto childNode in children.Nodes)
                {
                    string relativePath = RemoteTreePath.Combine(frame.ParentPath, childNode.Name);
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
                    RemoteTreeProgressReporter.ReportDirectory(
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
                    string relativePath = RemoteTreePath.Combine(frame.ParentPath, file.Name);
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
                    RemoteTreeProgressReporter.ReportFile(
                        progress,
                        filesScanned,
                        directoriesScanned,
                        pageMetrics,
                        relativePath,
                        entriesExpected);
                }

                int count = children.Nodes.Count + children.Files.Count;
                int loaded = frame.Loaded + count;
                if (count != 0 && loaded < pageRead.TotalCount)
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
    }
}
