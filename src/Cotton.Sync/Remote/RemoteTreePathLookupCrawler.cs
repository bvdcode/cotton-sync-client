// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk.Nodes;
using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    internal class RemoteTreePathLookupCrawler(ICottonNodeClient nodes, RemoteTreePageReader pages)
    {
        public async Task<RemoteTreeLookupSnapshot> CrawlAsync(
            Guid rootNodeId,
            IReadOnlyCollection<string> relativePaths,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            RemotePathLookupScope scope = RemotePathLookupScope.Create(relativePaths);
            RemoteTreeLookupSnapshot snapshot = new()
            {
                RootNode = await nodes.GetAsync(rootNodeId, cancellationToken).ConfigureAwait(false),
            };
            Stack<RemotePathLookupFrame> pending = new();
            pending.Push(new RemotePathLookupFrame(snapshot.RootNode, string.Empty, scope));
            RemoteTreePageReadMetrics metrics = RemoteTreePageReadMetrics.Empty;
            ReportProgress(progress, snapshot, metrics, string.Empty);

            while (pending.TryPop(out RemotePathLookupFrame? frame))
            {
                HashSet<string> remaining = new(frame.Scope.Children.Keys, StringComparer.OrdinalIgnoreCase);
                int page = 1;
                int loaded = 0;
                int? expectedTotalCount = null;
                while (frame.Scope.IncludesDescendants || remaining.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(progress, snapshot, metrics, frame.RelativePath);
                    RemoteTreePageReadResult result = await pages.ReadAsync(
                        frame.Node.Id, page, loaded, expectedTotalCount, cancellationToken).ConfigureAwait(false);
                    metrics = new RemoteTreePageReadMetrics(
                        metrics.PagesScanned + 1,
                        metrics.PageReadLatencyTotal + result.Elapsed,
                        result.Elapsed > metrics.PageReadLatencyMax ? result.Elapsed : metrics.PageReadLatencyMax,
                        result.Elapsed);
                    AddChildren(frame, result.Children, remaining, snapshot, pending);
                    ReportProgress(progress, snapshot, metrics, frame.RelativePath);
                    loaded += result.Children.Nodes.Count + result.Children.Files.Count;
                    if (loaded == result.TotalCount)
                    {
                        break;
                    }

                    expectedTotalCount = result.TotalCount;
                    page++;
                }
            }

            ReportProgress(progress, snapshot, metrics, string.Empty);
            return snapshot;
        }

        private static void AddChildren(
            RemotePathLookupFrame frame,
            NodeContentDto children,
            HashSet<string> remaining,
            RemoteTreeLookupSnapshot snapshot,
            Stack<RemotePathLookupFrame> pending)
        {
            foreach (NodeDto node in children.Nodes)
            {
                RemotePathLookupScope childScope = frame.Scope;
                if (!frame.Scope.IncludesDescendants)
                {
                    if (!remaining.Remove(node.Name))
                    {
                        continue;
                    }

                    childScope = frame.Scope.Children[node.Name];
                }

                string path = RemoteTreePath.Combine(frame.RelativePath, node.Name);
                if (SyncPathIgnoreRules.ShouldIgnore(path))
                {
                    continue;
                }

                if (snapshot.DirectoriesByPath.TryAdd(SyncPath.ToKey(path), new RemoteDirectorySnapshot
                {
                    RelativePath = path,
                    Node = node,
                }))
                {
                    pending.Push(new RemotePathLookupFrame(node, path, childScope));
                }
            }

            foreach (NodeFileManifestDto file in children.Files)
            {
                if (!frame.Scope.IncludesDescendants
                    && (!remaining.Remove(file.Name)
                        || !frame.Scope.Children[file.Name].IncludesDescendants))
                {
                    continue;
                }

                string path = RemoteTreePath.Combine(frame.RelativePath, file.Name);
                if (!SyncPathIgnoreRules.ShouldIgnore(path))
                {
                    snapshot.FilesByPath.TryAdd(SyncPath.ToKey(path), new RemoteFileSnapshot
                    {
                        RelativePath = path,
                        File = file,
                    });
                }
            }
        }

        private static void ReportProgress(
            IProgress<RemoteTreeScanProgress>? progress,
            RemoteTreeLookupSnapshot snapshot,
            RemoteTreePageReadMetrics metrics,
            string currentPath)
        {
            progress?.Report(new RemoteTreeScanProgress(
                snapshot.FilesByPath.Count,
                snapshot.DirectoriesByPath.Count,
                currentPath,
                metrics.PagesScanned,
                metrics.PageReadLatencyTotal,
                metrics.PageReadLatencyMax,
                metrics.LastPageReadLatency));
        }
    }
}
