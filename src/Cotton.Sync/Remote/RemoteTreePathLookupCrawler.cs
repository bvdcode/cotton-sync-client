// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk.Nodes;
using Cotton.Sync.State;

namespace Cotton.Sync.Remote
{
    internal class RemoteTreePathLookupCrawler
    {
        private readonly RemoteTreeDepthFirstCrawler _depthFirst;
        private readonly ICottonNodeClient _nodes;
        private readonly RemoteTreePageReader _pages;

        public RemoteTreePathLookupCrawler(
            ICottonNodeClient nodes,
            RemoteTreePageReader pages,
            RemoteTreeDepthFirstCrawler depthFirst)
        {
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            _pages = pages ?? throw new ArgumentNullException(nameof(pages));
            _depthFirst = depthFirst ?? throw new ArgumentNullException(nameof(depthFirst));
        }

        public async Task<RemoteTreeLookupSnapshot> CrawlAsync(
            Guid rootNodeId,
            IReadOnlyCollection<string> relativePaths,
            IProgress<RemoteTreeScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            RemoteTreeLookupSnapshot snapshot = new RemoteTreeLookupSnapshot
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

                RemotePathResolution resolution = await ResolveAsync(
                    snapshot.RootNode,
                    normalizedPath,
                    directory =>
                    {
                        if (TryAddDirectory(snapshot, directory))
                        {
                            directoriesScanned++;
                            RemoteTreeProgressReporter.ReportDirectory(
                                progress,
                                filesScanned,
                                directoriesScanned,
                                RemoteTreePageReadMetrics.Empty,
                                directory.RelativePath);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                if (resolution.File is not null)
                {
                    if (TryAddFile(snapshot, resolution.File))
                    {
                        filesScanned++;
                        RemoteTreeProgressReporter.ReportFile(
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
                    await _depthFirst.CrawlAsync(
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
                        resolution.Directory.RelativePath).ConfigureAwait(false);
                }
            }

            progress?.Report(new RemoteTreeScanProgress(filesScanned, directoriesScanned, currentPath: null));
            return snapshot;
        }

        private async Task<RemotePathResolution> ResolveAsync(
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
                NodeContentDto children = await _pages.FindContainingAsync(
                    currentNode.Id,
                    segment,
                    cancellationToken).ConfigureAwait(false);
                NodeDto? childDirectory = children.Nodes.FirstOrDefault(node =>
                    string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
                string childPath = RemoteTreePath.Combine(currentPath, segment);
                if (index == segments.Length - 1)
                {
                    if (childDirectory is not null)
                    {
                        RemoteDirectorySnapshot directory = new RemoteDirectorySnapshot
                        {
                            RelativePath = childPath,
                            Node = childDirectory,
                        };
                        addDirectory(directory);
                        return RemotePathResolution.ForDirectory(directory);
                    }

                    NodeFileManifestDto? file = children.Files.FirstOrDefault(item =>
                        string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
                    return file is null
                        ? RemotePathResolution.NotFound
                        : RemotePathResolution.ForFile(new RemoteFileSnapshot
                        {
                            RelativePath = childPath,
                            File = file,
                        });
                }

                if (childDirectory is null)
                {
                    return RemotePathResolution.NotFound;
                }

                currentPath = childPath;
                addDirectory(new RemoteDirectorySnapshot
                {
                    RelativePath = currentPath,
                    Node = childDirectory,
                });
                currentNode = childDirectory;
            }

            return RemotePathResolution.NotFound;
        }

        private static bool TryAddDirectory(
            RemoteTreeLookupSnapshot snapshot,
            RemoteDirectorySnapshot directory)
        {
            return snapshot.DirectoriesByPath.TryAdd(SyncPath.ToKey(directory.RelativePath), directory);
        }

        private static bool TryAddFile(RemoteTreeLookupSnapshot snapshot, RemoteFileSnapshot file)
        {
            return snapshot.FilesByPath.TryAdd(SyncPath.ToKey(file.RelativePath), file);
        }

        private record RemotePathResolution(RemoteDirectorySnapshot? Directory, RemoteFileSnapshot? File)
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
