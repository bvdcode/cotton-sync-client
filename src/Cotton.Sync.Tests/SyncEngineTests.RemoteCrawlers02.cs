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

        private class LookupOnlyRemoteTreeCrawler : IRemoteTreeLookupCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;

            public LookupOnlyRemoteTreeCrawler(RemoteTreeSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int LookupCrawlCalls { get; private set; }

            public int ProgressCrawlCalls { get; private set; }

            public int SnapshotCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                SnapshotCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeSnapshot> CrawlAsync(
                Guid rootNodeId,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                ProgressCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlLookupsAsync(
                Guid rootNodeId,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                LookupCrawlCalls++;
                RemoteTreeLookupSnapshot snapshot = new RemoteTreeLookupSnapshot
                {
                    RootNode = _snapshot.RootNode,
                };
                foreach (RemoteDirectorySnapshot directory in _snapshot.Directories)
                {
                    snapshot.DirectoriesByPath.Add(SyncPath.ToKey(directory.RelativePath), directory);
                }

                foreach (RemoteFileSnapshot file in _snapshot.Files)
                {
                    snapshot.FilesByPath.Add(SyncPath.ToKey(file.RelativePath), file);
                }

                return Task.FromResult(snapshot);
            }
        }


        private class PathOnlyRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;

            public PathOnlyRemoteTreeCrawler(RemoteTreeSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int FullCrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                FullCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                RemoteTreeLookupSnapshot snapshot = new RemoteTreeLookupSnapshot
                {
                    RootNode = _snapshot.RootNode,
                };
                foreach (RemoteDirectorySnapshot directory in _snapshot.Directories)
                {
                    if (relativePaths.Contains(directory.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in _snapshot.Files)
                {
                    if (relativePaths.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }
        }


        private class DescendantPathRemoteTreeCrawler : IRemoteTreeCrawler, IRemotePathLookupCrawler
        {
            private readonly RemoteTreeSnapshot _snapshot;

            public DescendantPathRemoteTreeCrawler(RemoteTreeSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public int FullCrawlCalls { get; private set; }

            public int PathCrawlCalls { get; private set; }

            public Task<RemoteTreeSnapshot> CrawlAsync(Guid rootNodeId, CancellationToken cancellationToken = default)
            {
                FullCrawlCalls++;
                return Task.FromResult(_snapshot);
            }

            public Task<RemoteTreeLookupSnapshot> CrawlPathLookupsAsync(
                Guid rootNodeId,
                IReadOnlyCollection<string> relativePaths,
                IProgress<RemoteTreeScanProgress>? progress,
                CancellationToken cancellationToken = default)
            {
                PathCrawlCalls++;
                RemoteTreeLookupSnapshot snapshot = new()
                {
                    RootNode = _snapshot.RootNode,
                };
                string[] requestedPaths = relativePaths.Select(SyncPath.Normalize).ToArray();
                foreach (RemoteDirectorySnapshot directory in _snapshot.Directories)
                {
                    if (requestedPaths.Any(path => ContainsRequestedPath(directory.RelativePath, path)))
                    {
                        snapshot.DirectoriesByPath[SyncPath.ToKey(directory.RelativePath)] = directory;
                    }
                }

                foreach (RemoteFileSnapshot file in _snapshot.Files)
                {
                    if (requestedPaths.Any(path => ContainsRequestedPath(file.RelativePath, path)))
                    {
                        snapshot.FilesByPath[SyncPath.ToKey(file.RelativePath)] = file;
                    }
                }

                return Task.FromResult(snapshot);
            }

            private static bool ContainsRequestedPath(string relativePath, string requestedPath)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                string normalizedRequestedPath = SyncPath.Normalize(requestedPath).TrimEnd('/');
                return normalizedPath.Equals(normalizedRequestedPath, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(normalizedRequestedPath + "/", StringComparison.OrdinalIgnoreCase)
                    || normalizedRequestedPath.StartsWith(normalizedPath + "/", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
