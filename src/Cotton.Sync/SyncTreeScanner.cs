// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class SyncTreeScanner(
        ILocalFileScanner localScanner,
        ILocalFileContentHasher? localContentHasher,
        ILocalFileMetadataTreeScanner? localMetadataTreeScanner,
        ILocalFileMetadataTreeLookupScanner? localMetadataTreeLookupScanner,
        ILocalFileMetadataPathLookupScanner? localMetadataPathLookupScanner,
        ILocalTreeScanner? localTreeScanner,
        IRemoteTreeCrawler remoteCrawler,
        IRemoteTreeLookupCrawler? remoteLookupCrawler,
        IRemotePathLookupCrawler? remotePathLookupCrawler,
        ILogger logger)
    {
        public async Task<SyncTreeLookups> ScanAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (!options.Scope.IsFull && options.Scope.LocalChangedPaths.Count > 0)
            {
                return await ScanScopedTreesAndBuildLookupsAsync(syncPair, options, startedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            }

            (Dictionary<string, LocalDirectorySnapshot> Directories, Dictionary<string, LocalFileSnapshot> Files) local =
                await ScanCompleteLocalTreeLookupsAsync(
                        syncPair.LocalRootPath,
                        options,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            (Dictionary<string, RemoteDirectorySnapshot> Directories, Dictionary<string, RemoteFileSnapshot> Files, NodeDto RootNode) remote =
                await ScanCompleteRemoteTreeLookupsAsync(
                        syncPair.RemoteRootNodeId,
                        options,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            ThrowIfPathKindCollisions(
                local.Directories,
                local.Files,
                directory => directory.RelativePath,
                file => file.RelativePath);
            ThrowIfPathKindCollisions(
                remote.Directories,
                remote.Files,
                directory => directory.RelativePath,
                file => file.RelativePath);
            return new SyncTreeLookups(
                local.Directories,
                remote.Directories,
                local.Files,
                remote.Files,
                remote.RootNode);
        }

        private async Task<(Dictionary<string, LocalDirectorySnapshot> Directories, Dictionary<string, LocalFileSnapshot> Files)>
            ScanCompleteLocalTreeLookupsAsync(
                string localRootPath,
                SyncRunOptions options,
                DateTime startedAtUtc,
                CancellationToken cancellationToken)
        {
            LocalTreeLookupSnapshot? lookups = await ScanLocalTreeLookupsAsync(
                    localRootPath,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lookups is not null)
            {
                return (lookups.DirectoriesByPath, lookups.FilesByPath);
            }

            LocalTreeSnapshot tree = await ScanLocalTreeAsync(localRootPath, options, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            return (
                ToDictionary(tree.Directories, directory => directory.RelativePath),
                ToDictionary(tree.Files, file => file.RelativePath));
        }

        private async Task<(
            Dictionary<string, RemoteDirectorySnapshot> Directories,
            Dictionary<string, RemoteFileSnapshot> Files,
            NodeDto RootNode)> ScanCompleteRemoteTreeLookupsAsync(
                Guid remoteRootNodeId,
                SyncRunOptions options,
                DateTime startedAtUtc,
                CancellationToken cancellationToken)
        {
            RemoteTreeLookupSnapshot? lookups = await ScanRemoteTreeLookupsAsync(
                    remoteRootNodeId,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lookups is not null)
            {
                return (lookups.DirectoriesByPath, lookups.FilesByPath, lookups.RootNode);
            }

            RemoteTreeSnapshot tree = await ScanRemoteTreeAsync(remoteRootNodeId, options, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            return (
                ToDictionary(tree.Directories, directory => directory.RelativePath),
                ToDictionary(tree.Files, file => file.RelativePath),
                tree.RootNode);
        }

        private async Task<SyncTreeLookups> ScanScopedTreesAndBuildLookupsAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (localMetadataPathLookupScanner is null || localContentHasher is null || remotePathLookupCrawler is null)
            {
                throw new InvalidOperationException(
                    "Scoped sync requires local path lookup, local content hashing, and remote path lookup capabilities.");
            }

            IReadOnlyList<string> scopedPaths = BuildScopedRelativePaths(options.Scope.LocalChangedPaths);
            SyncRunProgressReporter.Report(options, SyncRunProgressStage.ScanningLocal, 0, scopedPaths.Count, null, startedAtUtc);
            LocalTreeLookupSnapshot localTreeLookups = await localMetadataPathLookupScanner
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    scopedPaths,
                    new LocalTreeScanProgressReporter(options, startedAtUtc),
                    ShouldIncludeScopedDirectoryDescendants(syncPair),
                    cancellationToken)
                .ConfigureAwait(false);
            SyncRunProgressReporter.Report(options, SyncRunProgressStage.ScanningRemote, 0, scopedPaths.Count, null, startedAtUtc);
            RemoteTreeLookupSnapshot remoteTreeLookups = await remotePathLookupCrawler
                .CrawlPathLookupsAsync(
                    syncPair.RemoteRootNodeId,
                    scopedPaths,
                    new RemoteTreeScanProgressReporter(options, startedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfPathKindCollisions(
                localTreeLookups.DirectoriesByPath,
                localTreeLookups.FilesByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
            ThrowIfPathKindCollisions(
                remoteTreeLookups.DirectoriesByPath,
                remoteTreeLookups.FilesByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
            return new SyncTreeLookups(
                localTreeLookups.DirectoriesByPath,
                remoteTreeLookups.DirectoriesByPath,
                localTreeLookups.FilesByPath,
                remoteTreeLookups.FilesByPath,
                remoteTreeLookups.RootNode);
        }

        public async Task<LocalTreeSnapshot> ScanLocalTreeAsync(
            string localRootPath,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (localMetadataTreeScanner is ILocalFileMetadataTreeProgressScanner progressScanner && localContentHasher is not null)
            {
                return await progressScanner
                    .ScanTreeMetadataAsync(
                        localRootPath,
                        new LocalTreeScanProgressReporter(options, startedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (localMetadataTreeScanner is not null && localContentHasher is not null)
            {
                return await localMetadataTreeScanner.ScanTreeMetadataAsync(localRootPath, cancellationToken).ConfigureAwait(false);
            }

            if (localTreeScanner is not null)
            {
                return await localTreeScanner.ScanTreeAsync(localRootPath, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<LocalFileSnapshot> files = await localScanner.ScanAsync(localRootPath, cancellationToken).ConfigureAwait(false);
            return new LocalTreeSnapshot
            {
                Files = files.ToList(),
            };
        }

        public async Task<LocalTreeLookupSnapshot?> ScanLocalTreeLookupsAsync(
            string localRootPath,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (localMetadataTreeLookupScanner is null || localContentHasher is null)
            {
                return null;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            LocalTreeLookupSnapshot snapshot = await localMetadataTreeLookupScanner
                .ScanTreeMetadataLookupsAsync(
                    localRootPath,
                    new LocalTreeScanProgressReporter(options, startedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            logger.LogInformation(
                "Scanned local tree metadata for {LocalRootPath} with {DirectoryCount} directories and {FileCount} files in {ElapsedMilliseconds} ms.",
                localRootPath,
                snapshot.DirectoriesByPath.Count,
                snapshot.FilesByPath.Count,
                stopwatch.ElapsedMilliseconds);
            return snapshot;
        }

        private async Task<RemoteTreeSnapshot> ScanRemoteTreeAsync(
            Guid remoteRootNodeId,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            SyncRunProgressReporter.Report(options, SyncRunProgressStage.ScanningRemote, 0, null, null, startedAtUtc);
            if (remoteCrawler is IRemoteTreeProgressCrawler progressCrawler)
            {
                return await progressCrawler
                    .CrawlAsync(
                        remoteRootNodeId,
                        new RemoteTreeScanProgressReporter(options, startedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await remoteCrawler.CrawlAsync(remoteRootNodeId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<RemoteTreeLookupSnapshot?> ScanRemoteTreeLookupsAsync(
            Guid remoteRootNodeId,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (remoteLookupCrawler is null)
            {
                return null;
            }

            SyncRunProgressReporter.Report(options, SyncRunProgressStage.ScanningRemote, 0, null, null, startedAtUtc);
            return await remoteLookupCrawler
                .CrawlLookupsAsync(
                    remoteRootNodeId,
                    new RemoteTreeScanProgressReporter(options, startedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
