// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync
{
    /// <summary>
    /// Reconciles local and remote file snapshots for one synchronization pair.
    /// </summary>
    public class SyncEngine : ISyncEngine
    {
        private const int RunProgressDetailedItemInterval = 25;
        private const int RunProgressDetailedItemLimit = 50_000;
        private const int RunProgressSparseItemInterval = 100;
        private static readonly TimeSpan RunProgressReportTimeInterval = TimeSpan.FromMilliseconds(250);
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private readonly ILocalFileScanner _localScanner;
        private readonly ILocalFileContentHasher? _localContentHasher;
        private readonly ILocalFileContentHashProgressHasher? _localContentHashProgressHasher;
        private readonly ILocalFileMetadataTreeScanner? _localMetadataTreeScanner;
        private readonly ILocalFileMetadataTreeLookupScanner? _localMetadataTreeLookupScanner;
        private readonly ILocalFileMetadataPathLookupScanner? _localMetadataPathLookupScanner;
        private readonly ILocalTreeScanner? _localTreeScanner;
        private readonly IRemoteDirectorySynchronizer? _remoteDirectories;
        private readonly IRemoteTreeCrawler _remoteCrawler;
        private readonly IRemoteTreeLookupCrawler? _remoteLookupCrawler;
        private readonly IRemotePathLookupCrawler? _remotePathLookupCrawler;
        private readonly IRemoteTreeStreamingCrawler? _remoteStreamingCrawler;
        private readonly IRemoteFileSynchronizer _remoteFiles;
        private readonly ISyncStateStore _stateStore;
        private readonly ILocalFileSyncWriter _localWriter;
        private readonly IRemoteFilePlaceholderWriter? _remoteFilePlaceholderWriter;
        private readonly IRemoteFilePlaceholderPopulationObserver? _remoteFilePlaceholderPopulationObserver;
        private readonly IRemoteDirectoryMaterializationObserver? _remoteDirectoryMaterializationObserver;
        private readonly IRemoteDirectoryTreePopulationObserver? _remoteDirectoryTreePopulationObserver;
        private readonly ILogger<SyncEngine> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncEngine" /> class.
        /// </summary>
        public SyncEngine(
            ILocalFileScanner localScanner,
            IRemoteTreeCrawler remoteCrawler,
            IRemoteFileSynchronizer remoteFiles,
            ISyncStateStore stateStore,
            ILocalFileSyncWriter? localWriter = null,
            IRemoteDirectorySynchronizer? remoteDirectories = null,
            IRemoteFilePlaceholderWriter? remoteFilePlaceholderWriter = null,
            ILogger<SyncEngine>? logger = null)
        {
            _localScanner = localScanner ?? throw new ArgumentNullException(nameof(localScanner));
            _localContentHasher = localScanner as ILocalFileContentHasher;
            _localContentHashProgressHasher = localScanner as ILocalFileContentHashProgressHasher;
            _localMetadataTreeScanner = localScanner as ILocalFileMetadataTreeScanner;
            _localMetadataTreeLookupScanner = localScanner as ILocalFileMetadataTreeLookupScanner;
            _localMetadataPathLookupScanner = localScanner as ILocalFileMetadataPathLookupScanner;
            _localTreeScanner = localScanner as ILocalTreeScanner;
            _remoteCrawler = remoteCrawler ?? throw new ArgumentNullException(nameof(remoteCrawler));
            _remoteLookupCrawler = remoteCrawler as IRemoteTreeLookupCrawler;
            _remotePathLookupCrawler = remoteCrawler as IRemotePathLookupCrawler;
            _remoteStreamingCrawler = remoteCrawler as IRemoteTreeStreamingCrawler;
            _remoteFiles = remoteFiles ?? throw new ArgumentNullException(nameof(remoteFiles));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _localWriter = localWriter ?? new AtomicLocalFileSyncWriter();
            _remoteDirectories = remoteDirectories;
            _remoteFilePlaceholderWriter = remoteFilePlaceholderWriter;
            _remoteFilePlaceholderPopulationObserver =
                remoteFilePlaceholderWriter as IRemoteFilePlaceholderPopulationObserver;
            _remoteDirectoryMaterializationObserver =
                remoteFilePlaceholderWriter as IRemoteDirectoryMaterializationObserver;
            _remoteDirectoryTreePopulationObserver =
                remoteFilePlaceholderWriter as IRemoteDirectoryTreePopulationObserver;
            _logger = logger ?? NullLogger<SyncEngine>.Instance;
        }

        /// <inheritdoc />
        public async Task<SyncRunResult> RunOnceAsync(
            SyncPair syncPair,
            SyncRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPair.SyncPairId);
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPair.LocalRootPath);
            cancellationToken.ThrowIfCancellationRequested();

            options ??= new SyncRunOptions();
            ValidateOptions(options);
            DateTime startedAtUtc = DateTime.UtcNow;
            ReportRunProgress(options, SyncRunProgressStage.ScanningLocal, 0, null, null, startedAtUtc);
            _logger.LogInformation("Starting sync pass for pair {SyncPairId}.", syncPair.SyncPairId);
            await _stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncRunResult? initialVirtualFilesResult = await TryRunInitialWindowsVirtualFilesStreamingPopulationAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (initialVirtualFilesResult is not null)
            {
                _logger.LogInformation(
                    "Completed sync pass for pair {SyncPairId} with Windows virtual-files placeholder work: {ActivityCount} activities, 0 file content transfers.",
                    syncPair.SyncPairId,
                    initialVirtualFilesResult.TotalActivityCount);
                return initialVirtualFilesResult;
            }

            SyncTreeLookups treeLookups = await ScanTreesAndBuildLookupsAsync(syncPair, options, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            (Dictionary<string, SyncStateEntry> directoryStateByPath, Dictionary<string, SyncStateEntry> stateByPath) =
                await LoadStateByPathAsync(syncPair.SyncPairId, options, treeLookups, cancellationToken).ConfigureAwait(false);
            var result = new SyncRunResult();

            Dictionary<string, LocalDirectorySnapshot> localDirectoriesByPath = treeLookups.LocalDirectoriesByPath;
            Dictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath = treeLookups.RemoteDirectoriesByPath;
            Dictionary<string, LocalFileSnapshot> localByPath = treeLookups.LocalFilesByPath;
            Dictionary<string, RemoteFileSnapshot> remoteByPath = treeLookups.RemoteFilesByPath;
            IReadOnlyList<string> directoryPathKeys = BuildDirectoryPathKeys(
                localDirectoriesByPath.Keys,
                remoteDirectoriesByPath.Keys,
                directoryStateByPath.Keys);
            ReportRunProgress(options, SyncRunProgressStage.ReconcilingDirectories, 0, directoryPathKeys.Count, null, startedAtUtc);
            await ReconcileDirectoriesWithoutBaselineAsync(
                syncPair,
                options,
                result,
                directoryPathKeys,
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                directoryStateByPath,
                treeLookups.RemoteRootNode,
                startedAtUtc,
                cancellationToken).ConfigureAwait(false);

            await EnsureLocalContentHashesForStateFilesAsync(localByPath, stateByPath, options, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            await CoalesceLocalFileMovesAsync(
                syncPair,
                options,
                result,
                localByPath,
                remoteByPath,
                stateByPath,
                cancellationToken).ConfigureAwait(false);

            bool hasLocalDirectoryDeleteCandidates = HasLocalDirectoryDeleteCandidates(
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                directoryStateByPath);
            bool hasRemoteDirectoryDeleteCandidates = HasRemoteDirectoryDeleteCandidates(
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                directoryStateByPath);
            bool hasStaleDirectoryState = HasStaleDirectoryState(
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                directoryStateByPath);
            DirectoryContentIndex localDirectoryContentIndex = hasLocalDirectoryDeleteCandidates
                ? DirectoryContentIndex.Create(localDirectoriesByPath.Keys, localByPath.Keys)
                : DirectoryContentIndex.Empty;
            DirectoryContentIndex remoteDirectoryContentIndex = hasRemoteDirectoryDeleteCandidates
                ? DirectoryContentIndex.Create(remoteDirectoriesByPath.Keys, remoteByPath.Keys)
                : DirectoryContentIndex.Empty;

            SyncDeleteGuard deleteGuard = BuildDeleteGuard(
                options,
                localByPath,
                remoteByPath,
                stateByPath,
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                directoryStateByPath,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex);
            bool hasMissingRemoteOnlyPlaceholder = HasMissingRemoteOnlyPlaceholder(
                syncPair,
                localByPath,
                remoteByPath,
                stateByPath);

            if (hasLocalDirectoryDeleteCandidates || hasRemoteDirectoryDeleteCandidates || hasStaleDirectoryState)
            {
                await ReconcileDirectoryDeletesAsync(
                    syncPair,
                    options,
                    result,
                    deleteGuard,
                    directoryPathKeys,
                    localDirectoriesByPath,
                    remoteDirectoriesByPath,
                    directoryStateByPath,
                    localDirectoryContentIndex,
                    remoteDirectoryContentIndex,
                    cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<string> pathKeys = BuildPathKeys(localByPath.Keys, remoteByPath.Keys, stateByPath.Keys);
            EnsureEnoughLocalFreeSpaceForPlannedDownloads(
                syncPair,
                pathKeys,
                localByPath,
                remoteByPath,
                stateByPath);
            long plannedTransferBytesTotal = CalculatePlannedTransferBytesTotal(
                syncPair,
                pathKeys,
                localByPath,
                remoteByPath,
                stateByPath);
            long completedTransferBytes = 0;
            int filesCompleted = 0;
            DateTime? lastFileRunProgressReportedAtUtc = null;
            ReportRunProgress(
                options,
                SyncRunProgressStage.ReconcilingFiles,
                filesCompleted,
                pathKeys.Count,
                null,
                startedAtUtc,
                bytesCompleted: completedTransferBytes,
                bytesTotal: plannedTransferBytesTotal);
            foreach (string key in pathKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                localByPath.TryGetValue(key, out LocalFileSnapshot? local);
                remoteByPath.TryGetValue(key, out RemoteFileSnapshot? remote);
                stateByPath.TryGetValue(key, out SyncStateEntry? state);
                string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state?.RelativePath ?? key;
                SyncRunProgressStage fileProgressStage = ResolveFileRunProgressStage(syncPair, local, remote, state);
                long plannedTransferBytes = CalculatePlannedTransferBytes(syncPair, key, localByPath, remoteByPath, stateByPath);
                ReportItemRunProgress(
                    options,
                    fileProgressStage,
                    filesCompleted,
                    pathKeys.Count,
                    relativePath,
                    startedAtUtc,
                    ref lastFileRunProgressReportedAtUtc,
                    bytesCompleted: completedTransferBytes,
                    bytesTotal: plannedTransferBytesTotal);

                if (state is null)
                {
                    await ReconcileWithoutBaselineAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        remote,
                        hasMissingRemoteOnlyPlaceholder,
                        cancellationToken).ConfigureAwait(false);
                    filesCompleted++;
                    completedTransferBytes += plannedTransferBytes;
                    ReportItemRunProgress(
                        options,
                        fileProgressStage,
                        filesCompleted,
                        pathKeys.Count,
                        relativePath,
                        startedAtUtc,
                        ref lastFileRunProgressReportedAtUtc,
                        bytesCompleted: completedTransferBytes,
                        bytesTotal: plannedTransferBytesTotal);
                    await YieldAfterLargeBatchAsync(options, filesCompleted, pathKeys.Count, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await ReconcileWithBaselineAsync(syncPair, options, result, deleteGuard, state, relativePath, local, remote, cancellationToken)
                    .ConfigureAwait(false);
                filesCompleted++;
                completedTransferBytes += plannedTransferBytes;
                ReportItemRunProgress(
                    options,
                    fileProgressStage,
                    filesCompleted,
                    pathKeys.Count,
                    relativePath,
                    startedAtUtc,
                    ref lastFileRunProgressReportedAtUtc,
                    bytesCompleted: completedTransferBytes,
                    bytesTotal: plannedTransferBytesTotal);
                await YieldAfterLargeBatchAsync(options, filesCompleted, pathKeys.Count, cancellationToken)
                    .ConfigureAwait(false);
            }

            ReportRunProgress(
                options,
                SyncRunProgressStage.Completed,
                filesCompleted,
                pathKeys.Count,
                null,
                startedAtUtc,
                isCompleted: true,
                bytesCompleted: plannedTransferBytesTotal,
                bytesTotal: plannedTransferBytesTotal);
            _logger.LogInformation(
                "Completed sync pass for pair {SyncPairId} with {ActivityCount} activities.",
                syncPair.SyncPairId,
                result.TotalActivityCount);
            return result;
        }

        private async Task<SyncTreeLookups> ScanTreesAndBuildLookupsAsync(
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

            LocalTreeLookupSnapshot? localTreeLookups = await ScanLocalTreeLookupsAsync(
                    syncPair.LocalRootPath,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            LocalTreeSnapshot? localTree = null;
            if (localTreeLookups is null)
            {
                localTree = await ScanLocalTreeAsync(syncPair.LocalRootPath, options, startedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            }

            RemoteTreeLookupSnapshot? remoteTreeLookups = await ScanRemoteTreeLookupsAsync(
                    syncPair.RemoteRootNodeId,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            RemoteTreeSnapshot? remoteTree = null;
            if (remoteTreeLookups is null)
            {
                remoteTree = await ScanRemoteTreeAsync(syncPair.RemoteRootNodeId, options, startedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            }

            Dictionary<string, LocalDirectorySnapshot> localDirectoriesByPath = localTreeLookups?.DirectoriesByPath
                ?? ToDictionary(localTree!.Directories, directory => directory.RelativePath);
            Dictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath = remoteTreeLookups?.DirectoriesByPath
                ?? ToDictionary(remoteTree!.Directories, directory => directory.RelativePath);
            Dictionary<string, LocalFileSnapshot> localByPath = localTreeLookups?.FilesByPath
                ?? ToDictionary(localTree!.Files, file => file.RelativePath);
            Dictionary<string, RemoteFileSnapshot> remoteByPath = remoteTreeLookups?.FilesByPath
                ?? ToDictionary(remoteTree!.Files, file => file.RelativePath);
            ThrowIfPathKindCollisions(
                localDirectoriesByPath,
                localByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
            ThrowIfPathKindCollisions(
                remoteDirectoriesByPath,
                remoteByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
            return new SyncTreeLookups(
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                localByPath,
                remoteByPath,
                remoteTreeLookups?.RootNode ?? remoteTree!.RootNode);
        }

        private async Task<SyncTreeLookups> ScanScopedTreesAndBuildLookupsAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (_localMetadataPathLookupScanner is null || _localContentHasher is null || _remotePathLookupCrawler is null)
            {
                SyncRunOptions fullOptions = CloneAsFullScope(options);
                return await ScanTreesAndBuildLookupsAsync(syncPair, fullOptions, startedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            }

            IReadOnlyList<string> scopedPaths = BuildScopedRelativePaths(options.Scope.LocalChangedPaths);
            ReportRunProgress(options, SyncRunProgressStage.ScanningLocal, 0, scopedPaths.Count, null, startedAtUtc);
            LocalTreeLookupSnapshot localTreeLookups = await _localMetadataPathLookupScanner
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    scopedPaths,
                    new LocalTreeScanProgressReporter(options, startedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            ReportRunProgress(options, SyncRunProgressStage.ScanningRemote, 0, scopedPaths.Count, null, startedAtUtc);
            RemoteTreeLookupSnapshot remoteTreeLookups = await _remotePathLookupCrawler
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

        private async Task<(Dictionary<string, SyncStateEntry> DirectoryStateByPath, Dictionary<string, SyncStateEntry> FileStateByPath)> LoadStateByPathAsync(
            string syncPairId,
            SyncRunOptions options,
            SyncTreeLookups treeLookups,
            CancellationToken cancellationToken)
        {
            if (options.Scope.IsFull)
            {
                return await LoadAllStateByPathAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            }

            List<string> keys = BuildUniquePathKeyList(
                treeLookups.LocalDirectoriesByPath.Keys,
                treeLookups.RemoteDirectoriesByPath.Keys,
                treeLookups.LocalFilesByPath.Keys,
                treeLookups.RemoteFilesByPath.Keys,
                BuildScopedPathKeys(options.Scope.LocalChangedPaths));
            return await LoadStateByPathAsync(syncPairId, keys, cancellationToken).ConfigureAwait(false);
        }

        private async Task<(Dictionary<string, SyncStateEntry> DirectoryStateByPath, Dictionary<string, SyncStateEntry> FileStateByPath)> LoadAllStateByPathAsync(
            string syncPairId,
            CancellationToken cancellationToken)
        {
            var directoryStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            var fileStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            await foreach (SyncStateEntry entry in _stateStore.LoadPairEntriesAsync(syncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await _stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string key = SyncPath.ToKey(entry.RelativePath);
                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath.Add(key, entry);
                        break;
                    case SyncEntryKind.File:
                        fileStateByPath.Add(key, entry);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
            }

            return (directoryStateByPath, fileStateByPath);
        }

        private async Task<(Dictionary<string, SyncStateEntry> DirectoryStateByPath, Dictionary<string, SyncStateEntry> FileStateByPath)> LoadStateByPathAsync(
            string syncPairId,
            IEnumerable<string> keys,
            CancellationToken cancellationToken)
        {
            var directoryStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            var fileStateByPath = new Dictionary<string, SyncStateEntry>(PathComparer);
            await foreach (SyncStateEntry entry in _stateStore.LoadEntriesByPathKeysAsync(syncPairId, keys, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await _stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string stateKey = SyncPath.ToKey(entry.RelativePath);
                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath[stateKey] = entry;
                        break;
                    case SyncEntryKind.File:
                        fileStateByPath[stateKey] = entry;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
            }

            return (directoryStateByPath, fileStateByPath);
        }

        private async Task EnsureLocalContentHashesForStateFilesAsync(
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (stateByPath.Count == 0)
            {
                return;
            }

            int filesTotal = stateByPath.Count(state => localByPath.ContainsKey(state.Key));
            if (filesTotal == 0)
            {
                return;
            }

            int filesCompleted = 0;
            DateTime? lastReportedAtUtc = null;
            ReportItemRunProgress(
                options,
                SyncRunProgressStage.ScanningLocal,
                filesCompleted,
                filesTotal,
                currentPath: null,
                startedAtUtc,
                ref lastReportedAtUtc);

            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                if (localByPath.TryGetValue(state.Key, out LocalFileSnapshot? local))
                {
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ScanningLocal,
                        filesCompleted,
                        filesTotal,
                        local.RelativePath,
                        startedAtUtc,
                        ref lastReportedAtUtc);
                    if (!ShouldDeferLocalUpload(local, options, out _))
                    {
                        await EnsureLocalContentHashForBaselineComparisonAsync(local, state.Value, options, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    filesCompleted++;
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ScanningLocal,
                        filesCompleted,
                        filesTotal,
                        local.RelativePath,
                        startedAtUtc,
                        ref lastReportedAtUtc);
                }
            }
        }

        private async Task<LocalTreeSnapshot> ScanLocalTreeAsync(
            string localRootPath,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (_localMetadataTreeScanner is ILocalFileMetadataTreeProgressScanner progressScanner && _localContentHasher is not null)
            {
                return await progressScanner
                    .ScanTreeMetadataAsync(
                        localRootPath,
                        new LocalTreeScanProgressReporter(options, startedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_localMetadataTreeScanner is not null && _localContentHasher is not null)
            {
                return await _localMetadataTreeScanner.ScanTreeMetadataAsync(localRootPath, cancellationToken).ConfigureAwait(false);
            }

            if (_localTreeScanner is not null)
            {
                return await _localTreeScanner.ScanTreeAsync(localRootPath, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<LocalFileSnapshot> files = await _localScanner.ScanAsync(localRootPath, cancellationToken).ConfigureAwait(false);
            return new LocalTreeSnapshot
            {
                Files = files.ToList(),
            };
        }

        private async Task<LocalTreeLookupSnapshot?> ScanLocalTreeLookupsAsync(
            string localRootPath,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (_localMetadataTreeLookupScanner is null || _localContentHasher is null)
            {
                return null;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            LocalTreeLookupSnapshot snapshot = await _localMetadataTreeLookupScanner
                .ScanTreeMetadataLookupsAsync(
                    localRootPath,
                    new LocalTreeScanProgressReporter(options, startedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation(
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
            ReportRunProgress(options, SyncRunProgressStage.ScanningRemote, 0, null, null, startedAtUtc);
            if (_remoteCrawler is IRemoteTreeProgressCrawler progressCrawler)
            {
                return await progressCrawler
                    .CrawlAsync(
                        remoteRootNodeId,
                        new RemoteTreeScanProgressReporter(options, startedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _remoteCrawler.CrawlAsync(remoteRootNodeId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<RemoteTreeLookupSnapshot?> ScanRemoteTreeLookupsAsync(
            Guid remoteRootNodeId,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (_remoteLookupCrawler is null)
            {
                return null;
            }

            ReportRunProgress(options, SyncRunProgressStage.ScanningRemote, 0, null, null, startedAtUtc);
            return await _remoteLookupCrawler
                .CrawlLookupsAsync(
                    remoteRootNodeId,
                    new RemoteTreeScanProgressReporter(options, startedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<SyncRunResult?> TryRunInitialWindowsVirtualFilesStreamingPopulationAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            InitialVirtualFilesStreamingPlan? streamingPlan =
                await CreateInitialWindowsVirtualFilesStreamingPlanAsync(syncPair, options, startedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            if (streamingPlan is null)
            {
                return null;
            }

            long startingManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            _logger.LogInformation(
                "Starting initial streaming Windows virtual-files population for pair {SyncPairId} with queue capacity {QueueCapacity}, placeholder concurrency {PlaceholderConcurrency}, placeholder batch size {PlaceholderBatchSize}, state batch size {StateBatchSize}, and managed heap {ManagedHeapBytes} bytes.",
                syncPair.SyncPairId,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize,
                startingManagedHeapBytes);
            Stopwatch stopwatch = Stopwatch.StartNew();
            var result = new SyncRunResult();
            var channel = Channel.CreateBounded<InitialVirtualFilesPopulationItem>(
                new BoundedChannelOptions(options.InitialVirtualFilesPopulationQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
            int discoveredFiles = 0;
            int discoveredDirectories = 0;
            int completedFiles = 0;
            int createdPlaceholders = 0;
            int skippedCurrentPlaceholders = 0;
            int skippedUnavailablePlaceholders = 0;
            int stateFileRowsWritten = 0;
            int stateFileWriteBatches = 0;
            int stateDirectoryRowsWritten = 0;
            DateTime? lastPlaceholderProgressReportedAtUtc = null;
            var remoteScanProgress = new RemoteTreeScanProgressCounter();
            var initialVirtualFilesProgress = new InitialVirtualFilesRemoteProgressReporter(
                remoteScanProgress,
                options,
                startedAtUtc,
                () => Volatile.Read(ref completedFiles));
            ReportRunProgress(options, SyncRunProgressStage.CreatingPlaceholders, 0, null, null, startedAtUtc);

            using IDisposable? providerWriteBurst = _remoteFilePlaceholderPopulationObserver
                ?.BeginPopulation(syncPair.SyncPairId, syncPair.LocalRootPath);
            using var streamingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sink = new InitialVirtualFilesPopulationSink(
                channel.Writer,
                () => Interlocked.Increment(ref discoveredFiles),
                () => Interlocked.Increment(ref discoveredDirectories));
            Task producer = ProduceInitialWindowsVirtualFilesPopulationAsync(
                syncPair,
                options,
                startedAtUtc,
                channel,
                sink,
                initialVirtualFilesProgress,
                streamingCancellation.Token);
            Task consumer = ConsumeInitialWindowsVirtualFilesPopulationAsync(
                syncPair,
                options,
                result,
                channel.Reader,
                startedAtUtc,
                streamingPlan,
                () => Volatile.Read(ref discoveredFiles),
                () => Volatile.Read(ref completedFiles),
                value => Volatile.Write(ref completedFiles, value),
                () => lastPlaceholderProgressReportedAtUtc,
                value => lastPlaceholderProgressReportedAtUtc = value,
                workResult =>
                {
                    if (workResult.ActivityKind == SyncActivityKind.PlaceholderCreated && workResult.State is not null)
                    {
                        createdPlaceholders++;
                    }
                    else if (workResult.ActivityKind == SyncActivityKind.Skipped && workResult.ReportActivity)
                    {
                        skippedUnavailablePlaceholders++;
                    }
                    else if (workResult.ActivityKind == SyncActivityKind.Skipped)
                    {
                        skippedCurrentPlaceholders++;
                    }
                },
                writtenRows =>
                {
                    if (writtenRows > 0)
                    {
                        stateFileRowsWritten += writtenRows;
                        stateFileWriteBatches++;
                    }
                },
                () => stateDirectoryRowsWritten++,
                streamingCancellation.Token);

            Task firstCompleted = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
            if (firstCompleted.IsFaulted || firstCompleted.IsCanceled)
            {
                await streamingCancellation.CancelAsync().ConfigureAwait(false);
                channel.Writer.TryComplete(firstCompleted.Exception);
            }

            await Task.WhenAll(producer, consumer).ConfigureAwait(false);
            stopwatch.Stop();
            ReportRunProgress(
                options,
                SyncRunProgressStage.CreatingPlaceholders,
                completedFiles,
                completedFiles,
                null,
                startedAtUtc);
            ReportRunProgress(
                options,
                SyncRunProgressStage.Completed,
                completedFiles,
                completedFiles,
                null,
                startedAtUtc,
                isCompleted: true);
            double createdPlaceholderRatePerSecond = stopwatch.Elapsed.TotalSeconds <= 0d
                ? createdPlaceholders
                : createdPlaceholders / stopwatch.Elapsed.TotalSeconds;
            long completedManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            _logger.LogInformation(
                "Completed initial streaming Windows virtual-files population for pair {SyncPairId} with {DirectoryCount} directories discovered, {FileCount} files discovered, remote pages read={RemotePageCount}, {CompletedFileCount} file items completed, {CreatedPlaceholderCount} placeholders created or refreshed, {SkippedCurrentPlaceholderCount} current placeholders skipped, {SkippedUnavailablePlaceholderCount} placeholders skipped with user action in {ElapsedMilliseconds} ms at {CreatedPlaceholderRatePerSecond:F2} placeholders/sec; state writes {StateFileRowsWritten} file rows, file write batches {StateFileWriteBatchCount}, directory rows {StateDirectoryRowsWritten}; managed heap start={StartingManagedHeapBytes} bytes, completed={CompletedManagedHeapBytes} bytes, delta={ManagedHeapDeltaBytes} bytes; queue capacity={QueueCapacity}, placeholder concurrency={PlaceholderConcurrency}, placeholder batch size={PlaceholderBatchSize}, state batch size={StateBatchSize}; activities retained {RetainedActivityCount}/{TotalActivityCount}, truncated={ActivityListTruncated}.",
                syncPair.SyncPairId,
                Volatile.Read(ref discoveredDirectories),
                Volatile.Read(ref discoveredFiles),
                remoteScanProgress.PagesScanned,
                completedFiles,
                createdPlaceholders,
                skippedCurrentPlaceholders,
                skippedUnavailablePlaceholders,
                stopwatch.ElapsedMilliseconds,
                createdPlaceholderRatePerSecond,
                stateFileRowsWritten,
                stateFileWriteBatches,
                stateDirectoryRowsWritten,
                startingManagedHeapBytes,
                completedManagedHeapBytes,
                completedManagedHeapBytes - startingManagedHeapBytes,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize,
                result.Activities.Count,
                result.TotalActivityCount,
                result.IsActivityListTruncated);
            return result;
        }

        private async Task<InitialVirtualFilesStreamingPlan?> CreateInitialWindowsVirtualFilesStreamingPlanAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || !options.Scope.IsFull
                || _remoteStreamingCrawler is null
                || _remoteFilePlaceholderWriter is null)
            {
                return null;
            }

            InitialVirtualFilesStreamingPlan? stateFirstPlan =
                await TryCreateStateFirstWindowsVirtualFilesStreamingPlanAsync(syncPair, cancellationToken)
                    .ConfigureAwait(false);
            if (stateFirstPlan is not null)
            {
                return stateFirstPlan;
            }

            return await InspectLocalTreeForInitialWindowsVirtualFilesStreamingAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken).ConfigureAwait(false);
        }

        private async Task<InitialVirtualFilesStreamingPlan?> TryCreateStateFirstWindowsVirtualFilesStreamingPlanAsync(
            SyncPair syncPair,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int entriesSeen = 0;
            int directoryEntries = 0;
            int fileEntries = 0;
            int onlineOnlyFileEntries = 0;
            int materializedFileEntries = 0;
            var fileBaselineByPath = new Dictionary<string, InitialVirtualFilesPlaceholderBaseline>(PathComparer);
            InitialVirtualFilesStreamingPlan? FallBackToLocalTreeInspection(string reason)
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "Skipping Windows virtual-files state-first resume plan for pair {SyncPairId}: {Reason}. Entries seen={EntryStateCount}, directories={DirectoryStateCount}, files={FileStateCount}, online-only files={OnlineOnlyFileStateCount}, materialized files={MaterializedFileStateCount}, elapsed={ElapsedMilliseconds} ms.",
                    syncPair.SyncPairId,
                    reason,
                    entriesSeen,
                    directoryEntries,
                    fileEntries,
                    onlineOnlyFileEntries,
                    materializedFileEntries,
                    stopwatch.ElapsedMilliseconds);
                return null;
            }

            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.SyncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entriesSeen++;
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await _stateStore.DeleteAsync(syncPair.SyncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        if (entry.RemoteNodeId is null)
                        {
                            return FallBackToLocalTreeInspection("directory state is missing a remote folder id");
                        }

                        directoryEntries++;
                        break;
                    case SyncEntryKind.File:
                        fileEntries++;
                        if (!HasRemoteFileBaseline(entry))
                        {
                            return FallBackToLocalTreeInspection("file state is missing a remote baseline");
                        }

                        if (IsOnlineOnlyPlaceholderState(entry))
                        {
                            onlineOnlyFileEntries++;
                        }
                        else
                        {
                            materializedFileEntries++;
                        }

                        fileBaselineByPath[SyncPath.ToKey(entry.RelativePath)] =
                            InitialVirtualFilesPlaceholderBaseline.FromState(entry);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
            }

            stopwatch.Stop();
            if (entriesSeen == 0)
            {
                return FallBackToLocalTreeInspection("no persisted state entries");
            }

            _logger.LogInformation(
                "Loaded Windows virtual-files state-first resume plan for pair {SyncPairId} with {DirectoryStateCount} directories and {FileStateCount} files ({OnlineOnlyFileStateCount} online-only, {MaterializedFileStateCount} materialized) in {ElapsedMilliseconds} ms without scanning the local placeholder tree.",
                syncPair.SyncPairId,
                directoryEntries,
                fileEntries,
                onlineOnlyFileEntries,
                materializedFileEntries,
                stopwatch.ElapsedMilliseconds);
            return new InitialVirtualFilesStreamingPlan(
                SkipCurrentPlaceholders: true,
                CurrentPlaceholderBaselineByPath: fileBaselineByPath);
        }

        private async Task<InitialVirtualFilesStreamingPlan?> InspectLocalTreeForInitialWindowsVirtualFilesStreamingAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            LocalTreeLookupSnapshot? localTreeLookups = await ScanLocalTreeLookupsAsync(
                    syncPair.LocalRootPath,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (localTreeLookups is not null)
            {
                return await CreateInitialWindowsVirtualFilesStreamingPlanAsync(
                        syncPair,
                        localTreeLookups.DirectoriesByPath,
                        localTreeLookups.FilesByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            LocalTreeSnapshot localTree = await ScanLocalTreeAsync(syncPair.LocalRootPath, options, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            Dictionary<string, LocalDirectorySnapshot> directoriesByPath = localTree.Directories.ToDictionary(
                directory => SyncPath.ToKey(directory.RelativePath),
                PathComparer);
            Dictionary<string, LocalFileSnapshot> filesByPath = localTree.Files.ToDictionary(
                file => SyncPath.ToKey(file.RelativePath),
                PathComparer);
            return await CreateInitialWindowsVirtualFilesStreamingPlanAsync(
                    syncPair,
                    directoriesByPath,
                    filesByPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<InitialVirtualFilesStreamingPlan?> CreateInitialWindowsVirtualFilesStreamingPlanAsync(
            SyncPair syncPair,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, LocalFileSnapshot> localFilesByPath,
            CancellationToken cancellationToken)
        {
            if (localDirectoriesByPath.Count == 0 && localFilesByPath.Count == 0)
            {
                return new InitialVirtualFilesStreamingPlan(
                    SkipCurrentPlaceholders: false,
                    CurrentPlaceholderBaselineByPath: new Dictionary<string, InitialVirtualFilesPlaceholderBaseline>(PathComparer));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            (Dictionary<string, Guid?> directoryStateByPath, Dictionary<string, InitialVirtualFilesPlaceholderBaseline> fileBaselineByPath) =
                await LoadInitialVirtualFilesResumeStateByPathAsync(
                        syncPair.SyncPairId,
                        localDirectoriesByPath.Keys.Concat(localFilesByPath.Keys),
                        cancellationToken)
                    .ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation(
                "Loaded Windows virtual-files resume state matching the local tree for pair {SyncPairId} with {DirectoryStateCount} directories and {FileStateCount} files in {ElapsedMilliseconds} ms.",
                syncPair.SyncPairId,
                directoryStateByPath.Count,
                fileBaselineByPath.Count,
                stopwatch.ElapsedMilliseconds);
            foreach ((string fileKey, LocalFileSnapshot local) in localFilesByPath)
            {
                if (fileBaselineByPath.TryGetValue(fileKey, out InitialVirtualFilesPlaceholderBaseline baseline)
                    && IsResumeCompatibleVirtualFilesPlaceholder(local, baseline))
                {
                    continue;
                }

                if (IsUntrackedVirtualFilesPlaceholderCompatibleWithInitialStreaming(local))
                {
                    continue;
                }

                return null;
            }

            foreach (string directoryKey in localDirectoriesByPath.Keys)
            {
                if (directoryStateByPath.TryGetValue(directoryKey, out Guid? remoteNodeId)
                    && remoteNodeId is null)
                {
                    return null;
                }
            }

            return new InitialVirtualFilesStreamingPlan(
                SkipCurrentPlaceholders: true,
                CurrentPlaceholderBaselineByPath: fileBaselineByPath);
        }

        private async Task<(
            Dictionary<string, Guid?> DirectoryRemoteNodeIdByPath,
            Dictionary<string, InitialVirtualFilesPlaceholderBaseline> FileBaselineByPath)> LoadInitialVirtualFilesResumeStateByPathAsync(
            string syncPairId,
            IEnumerable<string> keys,
            CancellationToken cancellationToken)
        {
            var directoryStateByPath = new Dictionary<string, Guid?>(PathComparer);
            var fileBaselineByPath = new Dictionary<string, InitialVirtualFilesPlaceholderBaseline>(PathComparer);
            if (_stateStore is IVirtualFilesResumeStateStore virtualFilesResumeStateStore)
            {
                await foreach (SyncVirtualFilesResumeEntry entry in virtualFilesResumeStateStore.LoadVirtualFilesResumeEntriesByPathKeysAsync(syncPairId, keys, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                    {
                        await _stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    string stateKey = SyncPath.ToKey(entry.RelativePath);
                    switch (entry.Kind)
                    {
                        case SyncEntryKind.Directory:
                            directoryStateByPath[stateKey] = entry.RemoteNodeId;
                            break;
                        case SyncEntryKind.File:
                            fileBaselineByPath[stateKey] = InitialVirtualFilesPlaceholderBaseline.FromResumeEntry(entry);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                    }
                }

                return (directoryStateByPath, fileBaselineByPath);
            }

            await foreach (SyncStateEntry entry in _stateStore.LoadEntriesByPathKeysAsync(syncPairId, keys, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await _stateStore.DeleteAsync(syncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string stateKey = SyncPath.ToKey(entry.RelativePath);
                switch (entry.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath[stateKey] = entry.RemoteNodeId;
                        break;
                    case SyncEntryKind.File:
                        fileBaselineByPath[stateKey] = InitialVirtualFilesPlaceholderBaseline.FromState(entry);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown sync state entry kind.");
                }
            }

            return (directoryStateByPath, fileBaselineByPath);
        }

        private static bool IsUntrackedVirtualFilesPlaceholderCompatibleWithInitialStreaming(LocalFileSnapshot local)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder;
        }

        private static bool IsResumeCompatibleVirtualFilesPlaceholder(
            LocalFileSnapshot local,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder
                && IsOnlineOnlyPlaceholderState(baseline)
                && baseline.RemoteFileId.HasValue;
        }

        private async Task ProduceInitialWindowsVirtualFilesPopulationAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            Channel<InitialVirtualFilesPopulationItem> channel,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                await _remoteStreamingCrawler!
                    .CrawlStreamingAsync(
                        syncPair.RemoteRootNodeId,
                        sink,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
                throw;
            }
        }

        private async Task ConsumeInitialWindowsVirtualFilesPopulationAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            ChannelReader<InitialVirtualFilesPopulationItem> reader,
            DateTime startedAtUtc,
            InitialVirtualFilesStreamingPlan streamingPlan,
            Func<int> getDiscoveredFiles,
            Func<int> getCompletedFiles,
            Action<int> setCompletedFiles,
            Func<DateTime?> getLastPlaceholderProgressReportedAtUtc,
            Action<DateTime?> setLastPlaceholderProgressReportedAtUtc,
            Action<InitialVirtualFilesFileWorkResult> recordFileWorkResult,
            Action<int> recordFileStateWrite,
            Action recordDirectoryStateWrite,
            CancellationToken cancellationToken)
        {
            var pendingFileStates = new List<SyncStateEntry>(options.InitialVirtualFilesStateBatchSize);
            int placeholderBatchSize = _remoteFilePlaceholderWriter is IRemoteFilePlaceholderBatchWriter
                ? options.InitialVirtualFilesPlaceholderBatchSize
                : 1;
            var pendingFileBatch = new List<RemoteFileSnapshot>(placeholderBatchSize);
            var pendingFileTasks = new List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>>(
                options.InitialVirtualFilesPlaceholderConcurrency);
            Dictionary<string, RemoteDirectoryMaterializationRequest>? directoryTreeFinalizationRequests =
                _remoteDirectoryTreePopulationObserver is null
                    ? null
                    : new Dictionary<string, RemoteDirectoryMaterializationRequest>(PathComparer);
            try
            {
                await foreach (InitialVirtualFilesPopulationItem item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (item)
                    {
                        case InitialVirtualFilesDirectoryPopulationItem directoryItem:
                            EnqueueInitialVirtualFilesFileBatchWork(
                                pendingFileTasks,
                                pendingFileBatch,
                                syncPair,
                                options,
                                cancellationToken);
                            await DrainCompletedInitialVirtualFilesAsync(
                                    pendingFileTasks,
                                    pendingFileStates,
                                    syncPair,
                                    options,
                                    result,
                                    startedAtUtc,
                                    getDiscoveredFiles,
                                    getCompletedFiles,
                                    setCompletedFiles,
                                    getLastPlaceholderProgressReportedAtUtc,
                                    setLastPlaceholderProgressReportedAtUtc,
                                    recordFileWorkResult,
                                    recordFileStateWrite,
                                    waitForOne: false,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            int flushedFileRows =
                                await FlushInitialVirtualFilesStateBatchAsync(pendingFileStates, cancellationToken).ConfigureAwait(false);
                            recordFileStateWrite(flushedFileRows);
                            await CreateRemoteBackedLocalDirectoryAsync(
                                    syncPair,
                                    directoryItem.Directory.RelativePath,
                                    directoryItem.Directory.Node,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (directoryTreeFinalizationRequests is not null)
                            {
                                RemoteDirectoryMaterializationRequest finalizationRequest =
                                    CreateRemoteDirectoryMaterializationRequest(
                                        syncPair,
                                        directoryItem.Directory.RelativePath,
                                        directoryItem.Directory.Node);
                                directoryTreeFinalizationRequests[SyncPath.ToKey(finalizationRequest.RelativePath)] =
                                    finalizationRequest;
                            }

                            await _stateStore
                                .UpsertAsync(
                                    BuildDirectoryBaseline(
                                        syncPair,
                                        directoryItem.Directory.RelativePath,
                                        directoryItem.Directory.Node),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            recordDirectoryStateWrite();
                            break;

                        case InitialVirtualFilesFilePopulationItem fileItem:
                            if (!IsStreamingVirtualFilesBaselineAlreadyCurrent(fileItem.File, streamingPlan))
                            {
                                pendingFileBatch.Add(fileItem.File);
                                if (pendingFileBatch.Count >= placeholderBatchSize)
                                {
                                    EnqueueInitialVirtualFilesFileBatchWork(
                                        pendingFileTasks,
                                        pendingFileBatch,
                                        syncPair,
                                        options,
                                        cancellationToken);
                                }

                                if (pendingFileTasks.Count >= options.InitialVirtualFilesPlaceholderConcurrency)
                                {
                                    await DrainCompletedInitialVirtualFilesAsync(
                                            pendingFileTasks,
                                            pendingFileStates,
                                            syncPair,
                                            options,
                                            result,
                                            startedAtUtc,
                                            getDiscoveredFiles,
                                            getCompletedFiles,
                                            setCompletedFiles,
                                            getLastPlaceholderProgressReportedAtUtc,
                                            setLastPlaceholderProgressReportedAtUtc,
                                            recordFileWorkResult,
                                            recordFileStateWrite,
                                            waitForOne: true,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                }

                                break;
                            }

                            await CompleteInitialVirtualFilesFileWorkAsync(
                                    new InitialVirtualFilesFileWorkResult(fileItem.File.RelativePath, null, SyncActivityKind.Skipped, null, false, ReportActivity: false),
                                    pendingFileStates,
                                    syncPair,
                                    options,
                                    result,
                                    startedAtUtc,
                                    getDiscoveredFiles,
                                    getCompletedFiles,
                                    setCompletedFiles,
                                    getLastPlaceholderProgressReportedAtUtc,
                                    setLastPlaceholderProgressReportedAtUtc,
                                    recordFileWorkResult,
                                    recordFileStateWrite,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            break;
                    }
                }

                EnqueueInitialVirtualFilesFileBatchWork(
                    pendingFileTasks,
                    pendingFileBatch,
                    syncPair,
                    options,
                    cancellationToken);
                while (pendingFileTasks.Count > 0)
                {
                    await DrainCompletedInitialVirtualFilesAsync(
                            pendingFileTasks,
                            pendingFileStates,
                            syncPair,
                            options,
                            result,
                            startedAtUtc,
                            getDiscoveredFiles,
                            getCompletedFiles,
                            setCompletedFiles,
                            getLastPlaceholderProgressReportedAtUtc,
                            setLastPlaceholderProgressReportedAtUtc,
                            recordFileWorkResult,
                            recordFileStateWrite,
                            waitForOne: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                int finalFlushedFileRows =
                    await FlushInitialVirtualFilesStateBatchAsync(pendingFileStates, cancellationToken).ConfigureAwait(false);
                recordFileStateWrite(finalFlushedFileRows);
                if (directoryTreeFinalizationRequests is { Count: > 0 }
                    && _remoteDirectoryTreePopulationObserver is not null)
                {
                    await _remoteDirectoryTreePopulationObserver
                        .AfterDirectoryTreePopulationAsync(
                            directoryTreeFinalizationRequests.Values.ToArray(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                int flushedFileRows =
                    await FlushInitialVirtualFilesStateBatchAsync(pendingFileStates, cancellationToken).ConfigureAwait(false);
                recordFileStateWrite(flushedFileRows);
            }
        }

        private async Task DrainCompletedInitialVirtualFilesAsync(
            List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>> pendingFileTasks,
            List<SyncStateEntry> pendingFileStates,
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            DateTime startedAtUtc,
            Func<int> getDiscoveredFiles,
            Func<int> getCompletedFiles,
            Action<int> setCompletedFiles,
            Func<DateTime?> getLastPlaceholderProgressReportedAtUtc,
            Action<DateTime?> setLastPlaceholderProgressReportedAtUtc,
            Action<InitialVirtualFilesFileWorkResult> recordFileWorkResult,
            Action<int> recordFileStateWrite,
            bool waitForOne,
            CancellationToken cancellationToken)
        {
            if (pendingFileTasks.Count == 0)
            {
                return;
            }

            if (waitForOne)
            {
                Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> completedTask =
                    await Task.WhenAny(pendingFileTasks).ConfigureAwait(false);
                pendingFileTasks.Remove(completedTask);
                await CompleteInitialVirtualFilesFileWorkBatchAsync(
                        await completedTask.ConfigureAwait(false),
                        pendingFileStates,
                        syncPair,
                        options,
                        result,
                        startedAtUtc,
                        getDiscoveredFiles,
                        getCompletedFiles,
                        setCompletedFiles,
                        getLastPlaceholderProgressReportedAtUtc,
                        setLastPlaceholderProgressReportedAtUtc,
                        recordFileWorkResult,
                        recordFileStateWrite,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            for (int index = pendingFileTasks.Count - 1; index >= 0; index--)
            {
                Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> task = pendingFileTasks[index];
                if (!task.IsCompleted)
                {
                    continue;
                }

                pendingFileTasks.RemoveAt(index);
                await CompleteInitialVirtualFilesFileWorkBatchAsync(
                        await task.ConfigureAwait(false),
                        pendingFileStates,
                        syncPair,
                        options,
                        result,
                        startedAtUtc,
                        getDiscoveredFiles,
                        getCompletedFiles,
                        setCompletedFiles,
                        getLastPlaceholderProgressReportedAtUtc,
                        setLastPlaceholderProgressReportedAtUtc,
                        recordFileWorkResult,
                        recordFileStateWrite,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private void EnqueueInitialVirtualFilesFileBatchWork(
            List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>> pendingFileTasks,
            List<RemoteFileSnapshot> pendingFileBatch,
            SyncPair syncPair,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (pendingFileBatch.Count == 0)
            {
                return;
            }

            RemoteFileSnapshot[] batch = [.. pendingFileBatch];
            pendingFileBatch.Clear();
            pendingFileTasks.Add(CreateInitialVirtualFilesFileBatchWorkAsync(
                syncPair,
                options,
                batch,
                cancellationToken));
        }

        private Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> CreateInitialVirtualFilesFileBatchWorkAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            IReadOnlyList<RemoteFileSnapshot> remoteFiles,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                async () =>
                {
                    if (remoteFiles.Count == 0)
                    {
                        return Array.Empty<InitialVirtualFilesFileWorkResult>();
                    }

                    if (_remoteFilePlaceholderWriter is IRemoteFilePlaceholderBatchWriter batchWriter)
                    {
                        return await CreateInitialVirtualFilesBatchResultsAsync(
                                syncPair,
                                batchWriter,
                                remoteFiles,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
                    for (int index = 0; index < remoteFiles.Count; index++)
                    {
                        results[index] = await CreateInitialVirtualFilesFileResultAsync(
                                syncPair,
                                options,
                                remoteFiles[index],
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return results;
                },
                cancellationToken);
        }

        private async Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> CreateInitialVirtualFilesBatchResultsAsync(
            SyncPair syncPair,
            IRemoteFilePlaceholderBatchWriter batchWriter,
            IReadOnlyList<RemoteFileSnapshot> remoteFiles,
            CancellationToken cancellationToken)
        {
            var requests = new RemoteFilePlaceholderRequest[remoteFiles.Count];
            for (int index = 0; index < remoteFiles.Count; index++)
            {
                RemoteFileSnapshot remote = remoteFiles[index];
                requests[index] = CreateRemoteFilePlaceholderRequest(syncPair, remote.RelativePath, remote.File);
            }

            try
            {
                IReadOnlyList<RemoteFilePlaceholderBatchResult> batchResults =
                    await batchWriter.CreatePlaceholdersAsync(requests, cancellationToken).ConfigureAwait(false);
                if (batchResults.Count != remoteFiles.Count)
                {
                    throw new InvalidOperationException("Batch placeholder writer returned a different number of results.");
                }

                var results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
                for (int index = 0; index < remoteFiles.Count; index++)
                {
                    RemoteFileSnapshot remote = remoteFiles[index];
                    RemoteFilePlaceholderBatchResult batchResult = batchResults[index];
                    results[index] = batchResult.Placeholder is null
                        ? new InitialVirtualFilesFileWorkResult(
                            remote.RelativePath,
                            State: null,
                            SyncActivityKind.Skipped,
                            batchResult.UnavailableReason,
                            RequiresUserAction: true,
                            ReportActivity: true)
                        : new InitialVirtualFilesFileWorkResult(
                            remote.RelativePath,
                            BuildPlaceholderBaseline(syncPair, remote.RelativePath, remote.File, batchResult.Placeholder),
                            SyncActivityKind.PlaceholderCreated,
                            Details: null,
                            RequiresUserAction: false,
                            ReportActivity: false);
                }

                return results;
            }
            catch (RemoteFilePlaceholderUnavailableException exception)
            {
                var results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
                for (int index = 0; index < remoteFiles.Count; index++)
                {
                    results[index] = new InitialVirtualFilesFileWorkResult(
                        remoteFiles[index].RelativePath,
                        State: null,
                        SyncActivityKind.Skipped,
                        exception.Reason,
                        RequiresUserAction: true,
                        ReportActivity: true);
                }

                return results;
            }
        }

        private async Task<InitialVirtualFilesFileWorkResult> CreateInitialVirtualFilesFileResultAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            RemoteFileSnapshot remote,
            CancellationToken cancellationToken)
        {
            try
            {
                SyncStateEntry? placeholderState = await TryCreateRemoteOnlyFilePlaceholderStateAsync(
                        syncPair,
                        options,
                        remote.RelativePath,
                        remote.File,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new InitialVirtualFilesFileWorkResult(
                    remote.RelativePath,
                    placeholderState,
                    SyncActivityKind.PlaceholderCreated,
                    Details: null,
                    RequiresUserAction: false,
                    ReportActivity: false);
            }
            catch (RemoteFilePlaceholderUnavailableException exception)
            {
                return new InitialVirtualFilesFileWorkResult(
                    remote.RelativePath,
                    State: null,
                    SyncActivityKind.Skipped,
                    exception.Reason,
                    RequiresUserAction: true,
                    ReportActivity: true);
            }
        }

        private async Task CompleteInitialVirtualFilesFileWorkBatchAsync(
            IReadOnlyList<InitialVirtualFilesFileWorkResult> workResults,
            List<SyncStateEntry> pendingFileStates,
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            DateTime startedAtUtc,
            Func<int> getDiscoveredFiles,
            Func<int> getCompletedFiles,
            Action<int> setCompletedFiles,
            Func<DateTime?> getLastPlaceholderProgressReportedAtUtc,
            Action<DateTime?> setLastPlaceholderProgressReportedAtUtc,
            Action<InitialVirtualFilesFileWorkResult> recordFileWorkResult,
            Action<int> recordFileStateWrite,
            CancellationToken cancellationToken)
        {
            foreach (InitialVirtualFilesFileWorkResult workResult in workResults)
            {
                await CompleteInitialVirtualFilesFileWorkAsync(
                        workResult,
                        pendingFileStates,
                        syncPair,
                        options,
                        result,
                        startedAtUtc,
                        getDiscoveredFiles,
                        getCompletedFiles,
                        setCompletedFiles,
                        getLastPlaceholderProgressReportedAtUtc,
                        setLastPlaceholderProgressReportedAtUtc,
                        recordFileWorkResult,
                        recordFileStateWrite,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task CompleteInitialVirtualFilesFileWorkAsync(
            InitialVirtualFilesFileWorkResult workResult,
            List<SyncStateEntry> pendingFileStates,
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            DateTime startedAtUtc,
            Func<int> getDiscoveredFiles,
            Func<int> getCompletedFiles,
            Action<int> setCompletedFiles,
            Func<DateTime?> getLastPlaceholderProgressReportedAtUtc,
            Action<DateTime?> setLastPlaceholderProgressReportedAtUtc,
            Action<InitialVirtualFilesFileWorkResult> recordFileWorkResult,
            Action<int> recordFileStateWrite,
            CancellationToken cancellationToken)
        {
            recordFileWorkResult(workResult);

            if (workResult.State is not null)
            {
                pendingFileStates.Add(workResult.State);
                if (pendingFileStates.Count >= options.InitialVirtualFilesStateBatchSize)
                {
                    int flushedFileRows =
                        await FlushInitialVirtualFilesStateBatchAsync(pendingFileStates, cancellationToken).ConfigureAwait(false);
                    recordFileStateWrite(flushedFileRows);
                }
            }

            int completedFiles = getCompletedFiles() + 1;
            setCompletedFiles(completedFiles);
            bool reportedProgress = ReportStreamingPlaceholderProgress(
                options,
                completedFiles,
                getDiscoveredFiles(),
                workResult.RelativePath,
                startedAtUtc,
                getLastPlaceholderProgressReportedAtUtc(),
                setLastPlaceholderProgressReportedAtUtc);
            bool reportCreatedPlaceholderActivity =
                workResult.ActivityKind == SyncActivityKind.PlaceholderCreated && workResult.State is not null;
            if (workResult.ReportActivity || reportCreatedPlaceholderActivity)
            {
                Report(
                    result,
                    options,
                    workResult.ActivityKind,
                    workResult.RelativePath,
                    workResult.Details,
                    workResult.RequiresUserAction,
                    publishActivityProgress: workResult.ReportActivity || reportedProgress);
            }

            await YieldAfterLargeBatchAsync(
                    options,
                    completedFiles,
                    Math.Max(completedFiles, getDiscoveredFiles()),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<int> FlushInitialVirtualFilesStateBatchAsync(
            List<SyncStateEntry> pendingFileStates,
            CancellationToken cancellationToken)
        {
            if (pendingFileStates.Count == 0)
            {
                return 0;
            }

            int writtenRows = pendingFileStates.Count;
            await _stateStore.UpsertManyAsync(pendingFileStates, cancellationToken).ConfigureAwait(false);
            pendingFileStates.Clear();
            return writtenRows;
        }

        private async Task<SyncStateEntry?> TryCreateRemoteOnlyFilePlaceholderStateAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                throw new InvalidOperationException("Initial virtual-files placeholder creation requires Windows virtual-files materialization.");
            }

            if (_remoteFilePlaceholderWriter is null)
            {
                throw new RemoteFilePlaceholderUnavailableException(
                    relativePath,
                    "Windows virtual-files placeholder writer is not available.");
            }

            RemoteFilePlaceholderResult placeholder;
            try
            {
                placeholder = await _remoteFilePlaceholderWriter
                    .CreatePlaceholderAsync(
                        CreateRemoteFilePlaceholderRequest(syncPair, relativePath, remoteFile),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RemoteFilePlaceholderUnavailableException)
            {
                throw;
            }

            return BuildPlaceholderBaseline(syncPair, relativePath, remoteFile, placeholder, existingHydrationState);
        }

        private static RemoteFilePlaceholderRequest CreateRemoteFilePlaceholderRequest(
            SyncPair syncPair,
            string relativePath,
            NodeFileManifestDto remoteFile)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                SyncPath.Normalize(relativePath),
                remoteFile);
        }

        private async Task MaterializeRemoteOnlyFileAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                await DownloadAsync(syncPair, options, result, relativePath, remoteFile, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            SyncStateEntry? placeholderState;
            try
            {
                placeholderState = await TryCreateRemoteOnlyFilePlaceholderStateAsync(
                        syncPair,
                        options,
                        relativePath,
                        remoteFile,
                        cancellationToken,
                        existingHydrationState)
                    .ConfigureAwait(false);
            }
            catch (RemoteFilePlaceholderUnavailableException exception)
            {
                Report(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    exception.Reason,
                    requiresUserAction: true);
                return;
            }

            if (placeholderState is not null)
            {
                await _stateStore.UpsertAsync(placeholderState, cancellationToken).ConfigureAwait(false);
                Report(result, options, SyncActivityKind.PlaceholderCreated, relativePath, null);
            }
        }

        private static bool IsStreamingVirtualFilesBaselineAlreadyCurrent(
            RemoteFileSnapshot remote,
            InitialVirtualFilesStreamingPlan streamingPlan)
        {
            if (!streamingPlan.SkipCurrentPlaceholders)
            {
                return false;
            }

            string key = SyncPath.ToKey(remote.RelativePath);
            return streamingPlan.CurrentPlaceholderBaselineByPath.TryGetValue(
                    key,
                    out InitialVirtualFilesPlaceholderBaseline baseline)
                && HasRemoteFileBaseline(baseline)
                && RemoteMatchesBaseline(remote.File, baseline);
        }

        private static bool ReportStreamingPlaceholderProgress(
            SyncRunOptions options,
            int filesCompleted,
            int filesDiscovered,
            string relativePath,
            DateTime startedAtUtc,
            DateTime? lastReportedAtUtc,
            Action<DateTime?> setLastReportedAtUtc)
        {
            int filesTotal = Math.Max(filesCompleted, filesDiscovered);
            DateTime occurredAtUtc = DateTime.UtcNow;
            if (!ShouldReportItemRunProgress(filesCompleted, filesTotal, lastReportedAtUtc, occurredAtUtc))
            {
                return false;
            }

            setLastReportedAtUtc(occurredAtUtc);
            ReportRunProgress(
                options,
                SyncRunProgressStage.CreatingPlaceholders,
                filesCompleted,
                filesTotal,
                relativePath,
                startedAtUtc);
            return true;
        }

        private async Task ReconcileDirectoriesWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localByPath,
            IDictionary<string, RemoteDirectorySnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            NodeDto remoteRootNode,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            int foldersCompleted = 0;
            DateTime? lastDirectoryRunProgressReportedAtUtc = null;
            foreach (string key in pathKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                localByPath.TryGetValue(key, out LocalDirectorySnapshot? local);
                remoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                stateByPath.TryGetValue(key, out SyncStateEntry? state);
                string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state?.RelativePath ?? key;
                ReportItemRunProgress(
                    options,
                    SyncRunProgressStage.ReconcilingDirectories,
                    foldersCompleted,
                    pathKeys.Count,
                    relativePath,
                    startedAtUtc,
                    ref lastDirectoryRunProgressReportedAtUtc);
                if (state is not null)
                {
                    foldersCompleted++;
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ReconcilingDirectories,
                        foldersCompleted,
                        pathKeys.Count,
                        relativePath,
                        startedAtUtc,
                        ref lastDirectoryRunProgressReportedAtUtc);
                    continue;
                }

                if (local is null && remote is not null)
                {
                    await CreateRemoteBackedLocalDirectoryAsync(syncPair, relativePath, remote.Node, cancellationToken)
                        .ConfigureAwait(false);
                    await _stateStore.UpsertAsync(BuildDirectoryBaseline(syncPair, relativePath, remote.Node), cancellationToken)
                        .ConfigureAwait(false);
                    Report(result, options, SyncActivityKind.Downloaded, relativePath, "Created local folder.");
                    foldersCompleted++;
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ReconcilingDirectories,
                        foldersCompleted,
                        pathKeys.Count,
                        relativePath,
                        startedAtUtc,
                        ref lastDirectoryRunProgressReportedAtUtc);
                    continue;
                }

                if (local is not null && remote is null && _remoteDirectories is not null)
                {
                    string parentPath = GetParentPath(relativePath);
                    string parentKey = string.IsNullOrEmpty(parentPath) ? string.Empty : SyncPath.ToKey(parentPath);
                    if (!TryGetRemoteDirectoryNodeId(remoteByPath, parentKey, remoteRootNode.Id, out Guid parentNodeId))
                    {
                        foldersCompleted++;
                        ReportItemRunProgress(
                            options,
                            SyncRunProgressStage.ReconcilingDirectories,
                            foldersCompleted,
                            pathKeys.Count,
                            relativePath,
                            startedAtUtc,
                            ref lastDirectoryRunProgressReportedAtUtc);
                        continue;
                    }

                    RemoteDirectoryCreationResult creation = await CreateOrReuseRemoteDirectoryAsync(
                            _remoteDirectories,
                            parentNodeId,
                            GetFileName(relativePath),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var createdSnapshot = new RemoteDirectorySnapshot
                    {
                        RelativePath = relativePath,
                        Node = creation.Node,
                    };
                    remoteByPath[SyncPath.ToKey(relativePath)] = createdSnapshot;
                    await _stateStore.UpsertAsync(BuildDirectoryBaseline(syncPair, relativePath, creation.Node), cancellationToken)
                        .ConfigureAwait(false);
                    Report(
                        result,
                        options,
                        SyncActivityKind.Uploaded,
                        relativePath,
                        creation.ReusedExisting
                            ? "Reused existing remote folder after create conflict."
                            : "Created remote folder.");
                    foldersCompleted++;
                    ReportItemRunProgress(
                        options,
                        SyncRunProgressStage.ReconcilingDirectories,
                        foldersCompleted,
                        pathKeys.Count,
                        relativePath,
                        startedAtUtc,
                        ref lastDirectoryRunProgressReportedAtUtc);
                    continue;
                }

                if (local is not null && remote is not null)
                {
                    await _stateStore.UpsertAsync(BuildDirectoryBaseline(syncPair, relativePath, remote.Node), cancellationToken)
                        .ConfigureAwait(false);
                }

                foldersCompleted++;
                ReportItemRunProgress(
                    options,
                    SyncRunProgressStage.ReconcilingDirectories,
                    foldersCompleted,
                    pathKeys.Count,
                    relativePath,
                    startedAtUtc,
                    ref lastDirectoryRunProgressReportedAtUtc);
            }
        }

        private async Task<RemoteDirectoryCreationResult> CreateOrReuseRemoteDirectoryAsync(
            IRemoteDirectorySynchronizer remoteDirectories,
            Guid parentNodeId,
            string name,
            CancellationToken cancellationToken)
        {
            try
            {
                NodeDto created = await remoteDirectories
                    .CreateDirectoryAsync(parentNodeId, name, cancellationToken)
                    .ConfigureAwait(false);
                return new RemoteDirectoryCreationResult(created, ReusedExisting: false);
            }
            catch (CottonApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                NodeDto? existing = await remoteDirectories
                    .FindChildDirectoryAsync(parentNodeId, name, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }

                _logger.LogInformation(
                    "Remote folder create for {DirectoryName} under {ParentNodeId} hit conflict; reusing existing node {NodeId}.",
                    name,
                    parentNodeId,
                    existing.Id);
                return new RemoteDirectoryCreationResult(existing, ReusedExisting: true);
            }
        }

        private async Task CreateRemoteBackedLocalDirectoryAsync(
            SyncPair syncPair,
            string relativePath,
            NodeDto remoteDirectory,
            CancellationToken cancellationToken)
        {
            RemoteDirectoryMaterializationRequest? materializationRequest = null;
            if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && _remoteDirectoryMaterializationObserver is not null)
            {
                materializationRequest = CreateRemoteDirectoryMaterializationRequest(
                    syncPair,
                    relativePath,
                    remoteDirectory);
                await _remoteDirectoryMaterializationObserver
                    .BeforeCreateDirectoryAsync(materializationRequest, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _localWriter.CreateDirectoryAsync(syncPair.LocalRootPath, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (materializationRequest is not null)
            {
                await _remoteDirectoryMaterializationObserver!
                    .AfterCreateDirectoryAsync(materializationRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static RemoteDirectoryMaterializationRequest CreateRemoteDirectoryMaterializationRequest(
            SyncPair syncPair,
            string relativePath,
            NodeDto remoteDirectory)
        {
            return new RemoteDirectoryMaterializationRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                SyncPath.Normalize(relativePath),
                remoteDirectory);
        }

        private static bool TryGetRemoteDirectoryNodeId(
            IDictionary<string, RemoteDirectorySnapshot> remoteByPath,
            string key,
            Guid remoteRootNodeId,
            out Guid nodeId)
        {
            if (string.IsNullOrEmpty(key))
            {
                nodeId = remoteRootNodeId;
                return true;
            }

            if (remoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote))
            {
                nodeId = remote.Node.Id;
                return true;
            }

            nodeId = Guid.Empty;
            return false;
        }

        private async Task ReconcileDirectoryDeletesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            DirectoryContentIndex localDirectoryContentIndex,
            DirectoryContentIndex remoteDirectoryContentIndex,
            CancellationToken cancellationToken)
        {
            foreach (string key in EnumerateDirectoryDeleteKeys(pathKeys))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!stateByPath.TryGetValue(key, out SyncStateEntry? state))
                {
                    continue;
                }

                localByPath.TryGetValue(key, out LocalDirectorySnapshot? local);
                remoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state.RelativePath;

                if (local is null && remote is null)
                {
                    await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (local is null && remote is not null)
                {
                    await DeleteRemoteDirectoryAsync(
                        syncPair,
                        options,
                        result,
                        deleteGuard,
                        relativePath,
                        remote,
                        remoteDirectoryContentIndex,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (remote is null && local is not null)
                {
                    await DeleteLocalDirectoryAsync(
                        syncPair,
                        options,
                        result,
                        deleteGuard,
                        relativePath,
                        localDirectoryContentIndex,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task ReconcileWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            bool blockLocalOnlyUploads,
            CancellationToken cancellationToken)
        {
            if (local is not null && remote is null)
            {
                if (blockLocalOnlyUploads)
                {
                    Report(
                        result,
                        options,
                        SyncActivityKind.Skipped,
                        relativePath,
                        "Local upload skipped because a Windows virtual-files placeholder change in the same sync pass requires review.");
                    return;
                }

                await UploadAsync(syncPair, options, result, relativePath, local, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (local is null && remote is not null)
            {
                await MaterializeRemoteOnlyFileAsync(syncPair, options, result, relativePath, remote.File, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (local is not null && remote is not null)
            {
                await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
                if (ContentMatches(local.ContentHash, remote.File.ContentHash))
                {
                    await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remote.File), cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await PreserveConflictAsync(syncPair, options, result, relativePath, local, remote.File, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReconcileWithBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            SyncStateEntry state,
            string relativePath,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            CancellationToken cancellationToken)
        {
            if (local is not null)
            {
                await EnsureLocalContentHashForBaselineComparisonAsync(local, state, options, cancellationToken)
                    .ConfigureAwait(false);
            }

            bool localDeleted = local is null && !string.IsNullOrWhiteSpace(state.LocalContentHash);
            bool remoteDeleted = remote is null && state.RemoteFileId.HasValue;
            bool localChanged = local is not null && !ContentMatches(local.ContentHash, state.LocalContentHash);
            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            bool baselineDiverged = !ContentMatches(state.LocalContentHash, state.RemoteContentHash);

            if (local is null && remote is null)
            {
                await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (local is null
                && remote is not null
                && IsOnlineOnlyPlaceholderBaseline(syncPair, state))
            {
                if (remoteChanged)
                {
                    await MaterializeRemoteOnlyFileAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        remote.File,
                        cancellationToken,
                        state.PlaceholderHydrationState)
                        .ConfigureAwait(false);
                    return;
                }

                if (!options.Scope.IsFull)
                {
                    return;
                }

                Report(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    VirtualFileUserFacingCopy.RemoteOnlyLocalChangeRequiresActionMessage,
                    requiresUserAction: true);
                return;
            }

            if (remoteDeleted && IsOnlineOnlyPlaceholderBaseline(syncPair, state))
            {
                if (local is null)
                {
                    await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await DeleteLocalAsync(syncPair, options, result, deleteGuard, relativePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (local is not null
                && remote is not null
                && IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                if (remoteChanged)
                {
                    await MaterializeRemoteOnlyFileAsync(
                            syncPair,
                            options,
                            result,
                            relativePath,
                            remote.File,
                            cancellationToken,
                            state.PlaceholderHydrationState)
                        .ConfigureAwait(false);
                }

                return;
            }

            if (local is not null
                && remote is not null
                && IsOnlineOnlyPlaceholderBaseline(syncPair, state))
            {
                if (!remoteChanged)
                {
                    await UploadAsync(syncPair, options, result, relativePath, local, remote.File, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await PreserveConflictAsync(syncPair, options, result, relativePath, local, remote.File, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (local is not null && remote is not null && ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                if (!BaselineMatchesCurrentFile(syncPair, relativePath, state, local, remote.File))
                {
                    await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remote.File), cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            if (baselineDiverged)
            {
                if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
                {
                    return;
                }

                await PreserveConflictAsync(syncPair, options, result, relativePath, local, remote?.File, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
            {
                return;
            }

            if (localDeleted && remoteDeleted)
            {
                await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (localDeleted && !remoteChanged && remote is not null)
            {
                await DeleteRemoteAsync(syncPair, options, result, deleteGuard, relativePath, remote.File, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (remoteDeleted && !localChanged && local is not null)
            {
                await DeleteLocalAsync(syncPair, options, result, deleteGuard, relativePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (localDeleted && remoteChanged && remote is not null)
            {
                await PreserveConflictAsync(syncPair, options, result, relativePath, null, remote.File, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (remoteDeleted && localChanged && local is not null)
            {
                await PreserveConflictAsync(syncPair, options, result, relativePath, local, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (localChanged && !remoteChanged && local is not null)
            {
                await UploadAsync(syncPair, options, result, relativePath, local, remote?.File, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (remoteChanged && !localChanged && remote is not null)
            {
                await DownloadAsync(syncPair, options, result, relativePath, remote.File, cancellationToken).ConfigureAwait(false);
                return;
            }

            await PreserveConflictAsync(syncPair, options, result, relativePath, local, remote?.File, cancellationToken).ConfigureAwait(false);
        }

        private async Task CoalesceLocalFileMovesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, SyncStateEntry>> moveSources = FindLocalMoveSources(localByPath, remoteByPath, stateByPath);
            if (moveSources.Count == 0)
            {
                return;
            }

            Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>> candidates =
                await BuildLocalMoveCandidateBucketsAsync(localByPath, remoteByPath, stateByPath, options, cancellationToken)
                    .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, SyncStateEntry> source in moveSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!remoteByPath.TryGetValue(source.Key, out RemoteFileSnapshot? remote)
                    || string.IsNullOrWhiteSpace(source.Value.LocalContentHash)
                    || !source.Value.LocalSizeBytes.HasValue)
                {
                    continue;
                }

                var candidateKey = new MoveCandidateKey(source.Value.LocalContentHash, source.Value.LocalSizeBytes.Value);
                if (!candidates.TryGetValue(candidateKey, out Queue<LocalFileSnapshot>? bucket)
                    || !TryDequeueCurrentCandidate(bucket, remoteByPath, stateByPath, out LocalFileSnapshot? local))
                {
                    continue;
                }

                await MoveRemoteFileAsync(
                    syncPair,
                    options,
                    result,
                    source.Key,
                    source.Value,
                    local,
                    remote,
                    remoteByPath,
                    stateByPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static List<KeyValuePair<string, SyncStateEntry>> FindLocalMoveSources(
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath)
        {
            var result = new List<KeyValuePair<string, SyncStateEntry>>();
            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                if (state.Value.Kind != SyncEntryKind.File
                    || string.IsNullOrWhiteSpace(state.Value.LocalContentHash)
                    || !state.Value.LocalSizeBytes.HasValue
                    || localByPath.ContainsKey(state.Key)
                    || !remoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote)
                    || !RemoteMatchesBaseline(remote.File, state.Value))
                {
                    continue;
                }

                result.Add(state);
            }

            return result;
        }

        private async Task<Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>>> BuildLocalMoveCandidateBucketsAsync(
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            var candidates = new Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>>();
            foreach (KeyValuePair<string, LocalFileSnapshot> local in localByPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stateByPath.ContainsKey(local.Key) || remoteByPath.ContainsKey(local.Key))
                {
                    continue;
                }

                if (local.Value.IsCloudFilesOnlineOnlyPlaceholder)
                {
                    continue;
                }

                await EnsureLocalContentHashAsync(local.Value, options, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(local.Value.ContentHash))
                {
                    continue;
                }

                var candidateKey = new MoveCandidateKey(local.Value.ContentHash, local.Value.SizeBytes);
                if (!candidates.TryGetValue(candidateKey, out Queue<LocalFileSnapshot>? bucket))
                {
                    bucket = new Queue<LocalFileSnapshot>();
                    candidates[candidateKey] = bucket;
                }

                bucket.Enqueue(local.Value);
            }

            return candidates;
        }

        private static bool TryDequeueCurrentCandidate(
            Queue<LocalFileSnapshot> bucket,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            out LocalFileSnapshot local)
        {
            while (bucket.Count > 0)
            {
                LocalFileSnapshot candidate = bucket.Dequeue();
                string key = SyncPath.ToKey(candidate.RelativePath);
                if (!remoteByPath.ContainsKey(key) && !stateByPath.ContainsKey(key))
                {
                    local = candidate;
                    return true;
                }
            }

            local = null!;
            return false;
        }

        private async Task MoveRemoteFileAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string sourceKey,
            SyncStateEntry sourceState,
            LocalFileSnapshot local,
            RemoteFileSnapshot remote,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            string sourcePath = sourceState.RelativePath;
            string targetPath = local.RelativePath;
            NodeFileManifestDto moved;
            try
            {
                moved = await _remoteFiles
                    .MoveFileAsync(syncPair.RemoteRootNodeId, targetPath, remote.File, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsRemotePreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await FindLatestRemoteFileAsync(syncPair, sourcePath, cancellationToken).ConfigureAwait(false);
                if (latestRemoteFile is null)
                {
                    remoteByPath.Remove(sourceKey);
                }
                else
                {
                    remoteByPath[sourceKey] = new RemoteFileSnapshot
                    {
                        RelativePath = sourcePath,
                        File = latestRemoteFile,
                    };
                }

                return;
            }

            string targetKey = SyncPath.ToKey(targetPath);
            remoteByPath.Remove(sourceKey);
            remoteByPath[targetKey] = new RemoteFileSnapshot
            {
                RelativePath = targetPath,
                File = moved,
            };
            stateByPath.Remove(sourceKey);
            SyncStateEntry targetState = BuildBaseline(syncPair, targetPath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, moved);
            stateByPath[targetKey] = targetState;
            await _stateStore.DeleteAsync(syncPair.SyncPairId, sourcePath, cancellationToken).ConfigureAwait(false);
            await _stateStore.UpsertAsync(targetState, cancellationToken).ConfigureAwait(false);
            Report(result, options, SyncActivityKind.Moved, targetPath, "Moved from " + sourcePath + ".");
        }

        private async Task UploadAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto? existingRemoteFile,
            CancellationToken cancellationToken)
        {
            if (ShouldDeferLocalUpload(local, options, out TimeSpan remainingQuietTime))
            {
                ReportDeferredLocalUpload(result, options, relativePath, remainingQuietTime);
                return;
            }

            NodeFileManifestDto uploaded;
            try
            {
                uploaded = await UploadFileWithProgressAsync(
                    syncPair.RemoteRootNodeId,
                    relativePath,
                    local,
                    existingRemoteFile,
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (existingRemoteFile is not null && IsRemotePreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await FindLatestRemoteFileAsync(syncPair, relativePath, cancellationToken).ConfigureAwait(false);
                await PreserveConflictAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local,
                    latestRemoteFile ?? existingRemoteFile,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (LocalFileUnavailableException exception)
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, exception.Reason);
                result.RecordDeferredLocalPath(relativePath);
                return;
            }

            string localContentHash = ResolveUploadedLocalContentHash(local, uploaded);
            local.ContentHash = localContentHash;
            await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, localContentHash, local.LastWriteUtc, local.SizeBytes, uploaded), cancellationToken)
                .ConfigureAwait(false);
            Report(result, options, SyncActivityKind.Uploaded, relativePath, null);
        }

        private async Task DownloadAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, relativePath, remoteFile.SizeBytes);
            await _localWriter.WriteFileAsync(
                syncPair.LocalRootPath,
                relativePath,
                (stream, token) => DownloadAndVerifyFileAsync(remoteFile, relativePath, options, stream, token),
                remoteFile.UpdatedAt == default ? null : remoteFile.UpdatedAt,
                cancellationToken).ConfigureAwait(false);
            await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, remoteFile.ContentHash, remoteFile.UpdatedAt, remoteFile.SizeBytes, remoteFile), cancellationToken)
                .ConfigureAwait(false);
            Report(result, options, SyncActivityKind.Downloaded, relativePath, null);
        }

        private static SyncRunProgressStage ResolveFileRunProgressStage(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            SyncStateEntry? state)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || local is not null
                || remote is null)
            {
                return SyncRunProgressStage.ReconcilingFiles;
            }

            if (state is null
                || (IsOnlineOnlyPlaceholderBaseline(syncPair, state)
                    && !RemoteMatchesBaseline(remote.File, state)))
            {
                return SyncRunProgressStage.CreatingPlaceholders;
            }

            return SyncRunProgressStage.ReconcilingFiles;
        }

        private async Task DeleteRemoteAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, details, requiresUserAction: true);
                return;
            }

            try
            {
                await _remoteFiles.DeleteFileAsync(
                    remoteFile.Id,
                    options.DeleteRemotePermanently,
                    remoteFile.ETag,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsRemotePreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await FindLatestRemoteFileAsync(syncPair, relativePath, cancellationToken).ConfigureAwait(false);
                await PreserveConflictAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local: null,
                    remoteFile: latestRemoteFile ?? remoteFile,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            Report(result, options, SyncActivityKind.DeletedRemote, relativePath, null);
        }

        private async Task DeleteLocalAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!deleteGuard.CanDeleteLocal(out string? details))
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, details, requiresUserAction: true);
                return;
            }

            await _localWriter.DeleteFileAsync(syncPair.LocalRootPath, relativePath, cancellationToken).ConfigureAwait(false);
            await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            Report(result, options, SyncActivityKind.DeletedLocal, relativePath, null);
        }

        private async Task DeleteRemoteDirectoryAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            RemoteDirectorySnapshot remote,
            DirectoryContentIndex remoteDirectoryContentIndex,
            CancellationToken cancellationToken)
        {
            if (_remoteDirectories is null)
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, "Remote folder delete is not available.");
                return;
            }

            if (remoteDirectoryContentIndex.HasChildren(relativePath))
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, "Remote folder delete skipped because the folder is not empty.");
                return;
            }

            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, details, requiresUserAction: true);
                return;
            }

            await _remoteDirectories
                .DeleteDirectoryAsync(remote.Node.Id, options.DeleteRemotePermanently, cancellationToken)
                .ConfigureAwait(false);
            await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            Report(result, options, SyncActivityKind.DeletedRemote, relativePath, "Deleted remote folder.");
        }

        private async Task DeleteLocalDirectoryAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            DirectoryContentIndex localDirectoryContentIndex,
            CancellationToken cancellationToken)
        {
            if (localDirectoryContentIndex.HasChildren(relativePath))
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, "Local folder delete skipped because the folder is not empty.");
                return;
            }

            if (!deleteGuard.CanDeleteLocal(out string? details))
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, details, requiresUserAction: true);
                return;
            }

            await _localWriter.DeleteDirectoryAsync(syncPair.LocalRootPath, relativePath, cancellationToken).ConfigureAwait(false);
            await _stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            Report(result, options, SyncActivityKind.DeletedLocal, relativePath, "Deleted local folder.");
        }

        private async Task PreserveConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot? local,
            NodeFileManifestDto? remoteFile,
            CancellationToken cancellationToken)
        {
            string? details = null;
            if (local is not null && remoteFile is not null)
            {
                await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
                string conflictPath = _localWriter.CreateConflictRelativePath(syncPair.LocalRootPath, relativePath, DateTime.UtcNow);
                EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, conflictPath, remoteFile.SizeBytes);
                await _localWriter.WriteFileAsync(
                    syncPair.LocalRootPath,
                    conflictPath,
                    (stream, token) => DownloadAndVerifyFileAsync(remoteFile, relativePath, options, stream, token),
                    remoteFile.UpdatedAt == default ? null : remoteFile.UpdatedAt,
                    cancellationToken).ConfigureAwait(false);
                details = "Remote version saved as " + conflictPath;
                await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remoteFile), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (local is not null)
            {
                NodeFileManifestDto uploaded = await UploadFileWithProgressAsync(
                    syncPair.RemoteRootNodeId,
                    relativePath,
                    local,
                    null,
                    options,
                    cancellationToken).ConfigureAwait(false);
                details = "Remote deletion conflicted with local change; local version was uploaded again.";
                string localContentHash = ResolveUploadedLocalContentHash(local, uploaded);
                local.ContentHash = localContentHash;
                await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, localContentHash, local.LastWriteUtc, local.SizeBytes, uploaded), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (remoteFile is not null)
            {
                EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, relativePath, remoteFile.SizeBytes);
                await _localWriter.WriteFileAsync(
                    syncPair.LocalRootPath,
                    relativePath,
                    (stream, token) => DownloadAndVerifyFileAsync(remoteFile, relativePath, options, stream, token),
                    remoteFile.UpdatedAt == default ? null : remoteFile.UpdatedAt,
                    cancellationToken).ConfigureAwait(false);
                details = "Local deletion conflicted with remote change; remote version was restored locally.";
                await _stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, remoteFile.ContentHash, remoteFile.UpdatedAt, remoteFile.SizeBytes, remoteFile), cancellationToken)
                    .ConfigureAwait(false);
            }

            Report(result, options, SyncActivityKind.Conflict, relativePath, details);
        }

        private async Task<NodeFileManifestDto> UploadFileWithProgressAsync(
            Guid rootNodeId,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto? existingRemoteFile,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (_remoteFiles is IRemoteFileTransferProgressSynchronizer progressSynchronizer)
            {
                return await progressSynchronizer.UploadFileAsync(
                    rootNodeId,
                    relativePath,
                    local,
                    existingRemoteFile,
                    options.TransferProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            ReportTransfer(
                options,
                SyncTransferDirection.Upload,
                relativePath,
                transferredBytes: 0,
                totalBytes: local.SizeBytes);
            NodeFileManifestDto uploaded = await _remoteFiles.UploadFileAsync(
                rootNodeId,
                relativePath,
                local,
                existingRemoteFile,
                cancellationToken).ConfigureAwait(false);
            ReportTransfer(
                options,
                SyncTransferDirection.Upload,
                relativePath,
                local.SizeBytes,
                local.SizeBytes,
                isCompleted: true);
            return uploaded;
        }

        private async Task DownloadFileWithProgressAsync(
            NodeFileManifestDto remoteFile,
            string relativePath,
            SyncRunOptions options,
            Stream destination,
            CancellationToken cancellationToken)
        {
            if (_remoteFiles is IRemoteFileTransferProgressSynchronizer progressSynchronizer)
            {
                await progressSynchronizer.DownloadFileAsync(
                    remoteFile.Id,
                    relativePath,
                    remoteFile.SizeBytes,
                    destination,
                    options.TransferProgress,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            ReportTransfer(
                options,
                SyncTransferDirection.Download,
                relativePath,
                transferredBytes: 0,
                totalBytes: remoteFile.SizeBytes);
            await _remoteFiles.DownloadFileAsync(remoteFile.Id, destination, cancellationToken).ConfigureAwait(false);
            ReportTransfer(
                options,
                SyncTransferDirection.Download,
                relativePath,
                remoteFile.SizeBytes,
                remoteFile.SizeBytes,
                isCompleted: true);
        }

        private async Task DownloadAndVerifyFileAsync(
            NodeFileManifestDto remoteFile,
            string relativePath,
            SyncRunOptions options,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await using var verifiedDestination = new VerifyingDownloadStream(destination);
            await DownloadFileWithProgressAsync(remoteFile, relativePath, options, verifiedDestination, cancellationToken)
                .ConfigureAwait(false);
            verifiedDestination.Verify(remoteFile.ContentHash, remoteFile.SizeBytes, relativePath);
        }

        private async Task<NodeFileManifestDto?> FindLatestRemoteFileAsync(
            SyncPair syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            RemoteTreeSnapshot latestTree = await _remoteCrawler.CrawlAsync(syncPair.RemoteRootNodeId, cancellationToken).ConfigureAwait(false);
            string key = SyncPath.ToKey(relativePath);
            return latestTree.Files.FirstOrDefault(file => PathComparer.Equals(SyncPath.ToKey(file.RelativePath), key))?.File;
        }

        private static SyncStateEntry BuildBaseline(
            SyncPair syncPair,
            string relativePath,
            string? localContentHash,
            DateTime? localLastWriteUtc,
            long? localSizeBytes,
            NodeFileManifestDto? remoteFile)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.File,
                LocalContentHash = localContentHash,
                LocalLastWriteUtc = localLastWriteUtc?.ToUniversalTime(),
                LocalSizeBytes = localSizeBytes,
                RemoteSizeBytes = remoteFile?.SizeBytes,
                RemoteFileId = remoteFile?.Id,
                RemoteNodeId = remoteFile?.NodeId,
                RemoteContentHash = remoteFile?.ContentHash,
                RemoteETag = remoteFile?.ETag,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static SyncStateEntry BuildPlaceholderBaseline(
            SyncPair syncPair,
            string relativePath,
            NodeFileManifestDto remoteFile,
            RemoteFilePlaceholderResult placeholder,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            SyncPlaceholderHydrationState hydrationState = placeholder.HydrationState == SyncPlaceholderHydrationState.None
                ? SyncPlaceholderHydrationState.RemoteOnly
                : placeholder.HydrationState;
            if (existingHydrationState == SyncPlaceholderHydrationState.Dehydrated
                && hydrationState == SyncPlaceholderHydrationState.RemoteOnly)
            {
                hydrationState = SyncPlaceholderHydrationState.Dehydrated;
            }

            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.File,
                RemoteSizeBytes = remoteFile.SizeBytes,
                RemoteFileId = remoteFile.Id,
                RemoteNodeId = remoteFile.NodeId,
                RemoteContentHash = remoteFile.ContentHash,
                RemoteETag = remoteFile.ETag,
                PlaceholderIdentity = placeholder.PlaceholderIdentity,
                PlaceholderHydrationState = hydrationState,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static SyncStateEntry BuildDirectoryBaseline(
            SyncPair syncPair,
            string relativePath,
            NodeDto remoteNode)
        {
            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.Directory,
                RemoteNodeId = remoteNode.Id,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static int GetPathDepth(string relativePath)
        {
            return string.IsNullOrWhiteSpace(relativePath)
                ? 0
                : relativePath.Count(static character => character == '/') + 1;
        }

        private static string GetParentPath(string relativePath)
        {
            string normalized = SyncPath.Normalize(relativePath);
            int lastSlashIndex = normalized.LastIndexOf('/');
            return lastSlashIndex < 0 ? string.Empty : normalized[..lastSlashIndex];
        }

        private static string GetFileName(string relativePath)
        {
            string normalized = SyncPath.Normalize(relativePath);
            int lastSlashIndex = normalized.LastIndexOf('/');
            return lastSlashIndex < 0 ? normalized : normalized[(lastSlashIndex + 1)..];
        }

        private static bool RemoteMatchesBaseline(NodeFileManifestDto remoteFile, SyncStateEntry state)
        {
            if (!string.IsNullOrWhiteSpace(state.RemoteContentHash))
            {
                return ContentMatches(remoteFile.ContentHash, state.RemoteContentHash);
            }

            if (!string.IsNullOrWhiteSpace(state.RemoteETag))
            {
                return string.Equals(remoteFile.ETag, state.RemoteETag, StringComparison.Ordinal);
            }

            return state.RemoteFileId.HasValue && remoteFile.Id == state.RemoteFileId.Value;
        }

        private static bool RemoteMatchesBaseline(
            NodeFileManifestDto remoteFile,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            if (!string.IsNullOrWhiteSpace(baseline.RemoteContentHash))
            {
                return ContentMatches(remoteFile.ContentHash, baseline.RemoteContentHash);
            }

            if (!string.IsNullOrWhiteSpace(baseline.RemoteETag))
            {
                return string.Equals(remoteFile.ETag, baseline.RemoteETag, StringComparison.Ordinal);
            }

            return baseline.RemoteFileId.HasValue && remoteFile.Id == baseline.RemoteFileId.Value;
        }

        private static bool BaselineMatchesCurrentFile(
            SyncPair syncPair,
            string relativePath,
            SyncStateEntry state,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile)
        {
            return state.Kind == SyncEntryKind.File
                && string.Equals(state.SyncPairId, syncPair.SyncPairId, StringComparison.Ordinal)
                && PathComparer.Equals(SyncPath.ToKey(state.RelativePath), SyncPath.ToKey(relativePath))
                && ContentMatches(state.LocalContentHash, local.ContentHash)
                && NullableUtcEquals(state.LocalLastWriteUtc, local.LastWriteUtc)
                && state.LocalSizeBytes == local.SizeBytes
                && state.RemoteFileId == remoteFile.Id
                && state.RemoteNodeId == remoteFile.NodeId
                && ContentMatches(state.RemoteContentHash, remoteFile.ContentHash)
                && string.Equals(state.RemoteETag, remoteFile.ETag, StringComparison.Ordinal);
        }

        private static bool NullableUtcEquals(DateTime? left, DateTime? right)
        {
            return left?.ToUniversalTime() == right?.ToUniversalTime();
        }

        private static void ValidateOptions(SyncRunOptions options)
        {
            ArgumentNullException.ThrowIfNull(options.Scope);
            if (options.MinimumLocalUploadAge < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Minimum local upload age cannot be negative.");
            }

            if (options.MaximumLocalDeletesPerRun < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum local deletes per run cannot be negative.");
            }

            if (options.MaximumRemoteDeletesPerRun < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum remote deletes per run cannot be negative.");
            }

            if (options.MaximumStoredResultActivities < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Maximum stored result activities cannot be negative.");
            }

            if (options.InitialVirtualFilesPopulationQueueCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files population queue capacity must be positive.");
            }

            if (options.InitialVirtualFilesStateBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files state batch size must be positive.");
            }

            if (options.InitialVirtualFilesPlaceholderConcurrency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files placeholder concurrency must be positive.");
            }

            if (options.InitialVirtualFilesPlaceholderBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Initial virtual-files placeholder batch size must be positive.");
            }
        }

        private static void EnsureEnoughLocalFreeSpace(string localRootPath, string relativePath, long requiredBytes)
        {
            if (requiredBytes <= 0)
            {
                return;
            }

            long? availableFreeBytes = TryGetAvailableFreeBytes(localRootPath);
            if (!availableFreeBytes.HasValue || availableFreeBytes.Value >= requiredBytes)
            {
                return;
            }

            string displayPath = string.IsNullOrWhiteSpace(relativePath) ? "remote file" : relativePath;
            throw new LocalInsufficientDiskSpaceException(displayPath, requiredBytes, availableFreeBytes.Value);
        }

        private static long? TryGetAvailableFreeBytes(string localRootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            try
            {
                string fullRoot = Path.GetFullPath(localRootPath);
                Directory.CreateDirectory(fullRoot);
                string? driveRoot = Path.GetPathRoot(fullRoot);
                if (string.IsNullOrWhiteSpace(driveRoot))
                {
                    return null;
                }

                var drive = new DriveInfo(driveRoot);
                return drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void EnsureEnoughLocalFreeSpaceForPlannedDownloads(
            SyncPair syncPair,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            long? availableFreeBytes = TryGetAvailableFreeBytes(syncPair.LocalRootPath);
            if (!availableFreeBytes.HasValue)
            {
                return;
            }

            long simulatedFreeBytes = availableFreeBytes.Value;
            foreach (string key in pathKeys)
            {
                if (!TryCreatePlannedLocalDownload(
                        syncPair,
                        key,
                        localByPath,
                        remoteByPath,
                        stateByPath,
                        out string relativePath,
                        out long downloadBytes,
                        out long replacedLocalBytes))
                {
                    continue;
                }

                if (downloadBytes <= 0)
                {
                    continue;
                }

                if (simulatedFreeBytes < downloadBytes)
                {
                    throw new LocalInsufficientDiskSpaceException(relativePath, downloadBytes, simulatedFreeBytes);
                }

                simulatedFreeBytes += replacedLocalBytes - downloadBytes;
            }
        }

        private static long CalculatePlannedTransferBytesTotal(
            SyncPair syncPair,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            long totalBytes = 0;
            foreach (string key in pathKeys)
            {
                if (TryCalculatePlannedTransferBytes(syncPair, key, localByPath, remoteByPath, stateByPath, out long transferBytes)
                    && transferBytes > 0)
                {
                    totalBytes += transferBytes;
                }
            }

            return totalBytes;
        }

        private static long CalculatePlannedTransferBytes(
            SyncPair syncPair,
            string key,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            return TryCalculatePlannedTransferBytes(syncPair, key, localByPath, remoteByPath, stateByPath, out long transferBytes)
                ? transferBytes
                : 0;
        }

        private static bool TryCalculatePlannedTransferBytes(
            SyncPair syncPair,
            string key,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            out long transferBytes)
        {
            localByPath.TryGetValue(key, out LocalFileSnapshot? local);
            remoteByPath.TryGetValue(key, out RemoteFileSnapshot? remote);
            stateByPath.TryGetValue(key, out SyncStateEntry? state);

            if (state is null)
            {
                return TryCalculateUntrackedTransferBytes(syncPair, local, remote, out transferBytes);
            }

            if (local is not null && IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                if (remote is not null && !RemoteMatchesBaseline(remote.File, state))
                {
                    transferBytes = remote.File.SizeBytes;
                    return true;
                }

                transferBytes = 0;
                return false;
            }

            if (local is not null && remote is not null && ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                transferBytes = 0;
                return false;
            }

            bool localDeleted = local is null && !string.IsNullOrWhiteSpace(state.LocalContentHash);
            bool remoteDeleted = remote is null && state.RemoteFileId.HasValue;
            bool localChanged = local is not null && !ContentMatches(local.ContentHash, state.LocalContentHash);
            bool remoteChanged = remote is not null && RemoteMatchesBaseline(remote.File, state) is false;
            bool baselineDiverged = !ContentMatches(state.LocalContentHash, state.RemoteContentHash);

            if (baselineDiverged)
            {
                if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
                {
                    transferBytes = 0;
                    return false;
                }

                return TryCalculateConflictTransferBytes(local, remote?.File, out transferBytes);
            }

            if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
            {
                transferBytes = 0;
                return false;
            }

            if (localDeleted && remoteChanged)
            {
                return TryCalculateConflictTransferBytes(local, remote?.File, out transferBytes);
            }

            if (remoteDeleted && localChanged && local is not null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (localChanged && !remoteChanged && local is not null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (remoteChanged && !localChanged && remote is not null)
            {
                transferBytes = remote.File.SizeBytes;
                return true;
            }

            if ((localChanged && remoteChanged) || (localDeleted && remoteChanged) || (remoteDeleted && localChanged))
            {
                return TryCalculateConflictTransferBytes(local, remote?.File, out transferBytes);
            }

            transferBytes = 0;
            return false;
        }

        private static bool TryCalculateUntrackedTransferBytes(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (local is not null && remote is null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (local is null && remote is not null)
            {
                if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles)
                {
                    transferBytes = 0;
                    return false;
                }

                transferBytes = remote.File.SizeBytes;
                return true;
            }

            if (local is not null
                && remote is not null
                && !string.IsNullOrWhiteSpace(local.ContentHash)
                && !ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                transferBytes = remote.File.SizeBytes;
                return true;
            }

            transferBytes = 0;
            return false;
        }

        private static bool TryCalculateConflictTransferBytes(
            LocalFileSnapshot? local,
            NodeFileManifestDto? remoteFile,
            out long transferBytes)
        {
            if (local is not null && remoteFile is null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (remoteFile is not null)
            {
                transferBytes = remoteFile.SizeBytes;
                return true;
            }

            transferBytes = 0;
            return false;
        }

        private static bool TryCreatePlannedLocalDownload(
            SyncPair syncPair,
            string key,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            out string relativePath,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            localByPath.TryGetValue(key, out LocalFileSnapshot? local);
            remoteByPath.TryGetValue(key, out RemoteFileSnapshot? remote);
            stateByPath.TryGetValue(key, out SyncStateEntry? state);
            relativePath = local?.RelativePath ?? remote?.RelativePath ?? state?.RelativePath ?? key;

            if (state is null)
            {
                return TryCreateRemoteOnlyDownload(syncPair, local, remote, out downloadBytes, out replacedLocalBytes);
            }

            if (local is not null && IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                bool placeholderRemoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
                downloadBytes = placeholderRemoteChanged ? remote!.File.SizeBytes : 0;
                replacedLocalBytes = 0;
                return placeholderRemoteChanged;
            }

            if (local is not null && remote is not null && ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            bool localDeleted = local is null && !string.IsNullOrWhiteSpace(state.LocalContentHash);
            bool remoteDeleted = remote is null && state.RemoteFileId.HasValue;
            bool localChanged = local is not null && !ContentMatches(local.ContentHash, state.LocalContentHash);
            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            bool baselineDiverged = !ContentMatches(state.LocalContentHash, state.RemoteContentHash);

            if (baselineDiverged)
            {
                if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
                {
                    downloadBytes = 0;
                    replacedLocalBytes = 0;
                    return false;
                }

                return TryCreateConflictDownload(remote, out downloadBytes, out replacedLocalBytes);
            }

            if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            if (localDeleted && remoteChanged)
            {
                return TryCreateConflictDownload(remote, out downloadBytes, out replacedLocalBytes);
            }

            if (remoteChanged && !localChanged && remote is not null)
            {
                downloadBytes = remote.File.SizeBytes;
                replacedLocalBytes = local?.SizeBytes ?? 0;
                return true;
            }

            downloadBytes = 0;
            replacedLocalBytes = 0;
            return false;
        }

        private static bool TryCreateRemoteOnlyDownload(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            if (local is null && remote is not null)
            {
                if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles)
                {
                    downloadBytes = 0;
                    replacedLocalBytes = 0;
                    return false;
                }

                downloadBytes = remote.File.SizeBytes;
                replacedLocalBytes = 0;
                return true;
            }

            downloadBytes = 0;
            replacedLocalBytes = 0;
            return false;
        }

        private static bool TryCreateConflictDownload(
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            if (remote is null)
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            downloadBytes = remote.File.SizeBytes;
            replacedLocalBytes = 0;
            return true;
        }

        private static SyncDeleteGuard BuildDeleteGuard(
            SyncRunOptions options,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath,
            DirectoryContentIndex localDirectoryContentIndex,
            DirectoryContentIndex remoteDirectoryContentIndex)
        {
            if (stateByPath.Count == 0 && directoryStateByPath.Count == 0)
            {
                return new SyncDeleteGuard(options, plannedLocalDeletes: 0, plannedRemoteDeletes: 0);
            }

            int plannedLocalDeletes = 0;
            int plannedRemoteDeletes = 0;

            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                localByPath.TryGetValue(state.Key, out LocalFileSnapshot? local);
                remoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote);

                switch (GetPlannedDeleteDirection(state.Value, local, remote))
                {
                    case SyncDeleteDirection.Local:
                        plannedLocalDeletes++;
                        break;
                    case SyncDeleteDirection.Remote:
                        plannedRemoteDeletes++;
                        break;
                }
            }

            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                localDirectoriesByPath.TryGetValue(state.Key, out LocalDirectorySnapshot? local);
                remoteDirectoriesByPath.TryGetValue(state.Key, out RemoteDirectorySnapshot? remote);

                switch (GetPlannedDirectoryDeleteDirection(
                    state.Value,
                    local,
                    remote,
                    localDirectoryContentIndex,
                    remoteDirectoryContentIndex))
                {
                    case SyncDeleteDirection.Local:
                        plannedLocalDeletes++;
                        break;
                    case SyncDeleteDirection.Remote:
                        plannedRemoteDeletes++;
                        break;
                }
            }

            return new SyncDeleteGuard(options, plannedLocalDeletes, plannedRemoteDeletes);
        }

        private static bool HasLocalDirectoryDeleteCandidates(
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (state.Value.RemoteNodeId is null)
                {
                    continue;
                }

                if (localDirectoriesByPath.ContainsKey(state.Key) && !remoteDirectoriesByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRemoteDirectoryDeleteCandidates(
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (state.Value.RemoteNodeId is null)
                {
                    continue;
                }

                if (!localDirectoriesByPath.ContainsKey(state.Key) && remoteDirectoriesByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasStaleDirectoryState(
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (!localDirectoriesByPath.ContainsKey(state.Key) && !remoteDirectoriesByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        private static SyncDeleteDirection GetPlannedDirectoryDeleteDirection(
            SyncStateEntry state,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote,
            DirectoryContentIndex localDirectoryContentIndex,
            DirectoryContentIndex remoteDirectoryContentIndex)
        {
            if (state.RemoteNodeId is null || local is null && remote is null)
            {
                return SyncDeleteDirection.None;
            }

            string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state.RelativePath;
            if (local is null && remote is not null && !remoteDirectoryContentIndex.HasChildren(relativePath))
            {
                return SyncDeleteDirection.Remote;
            }

            if (remote is null && local is not null && !localDirectoryContentIndex.HasChildren(relativePath))
            {
                return SyncDeleteDirection.Local;
            }

            return SyncDeleteDirection.None;
        }

        private static SyncDeleteDirection GetPlannedDeleteDirection(
            SyncStateEntry? state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            if (state is null)
            {
                return SyncDeleteDirection.None;
            }

            if (local is null && remote is null)
            {
                return SyncDeleteDirection.None;
            }

            if (local is null && remote is not null && IsOnlineOnlyPlaceholderState(state))
            {
                return SyncDeleteDirection.None;
            }

            if (local is not null && remote is not null && ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                return SyncDeleteDirection.None;
            }

            bool localDeleted = local is null && !string.IsNullOrWhiteSpace(state.LocalContentHash);
            bool remoteDeleted = remote is null && state.RemoteFileId.HasValue;
            bool localChanged = local is not null && !ContentMatches(local.ContentHash, state.LocalContentHash);
            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            bool baselineDiverged = !ContentMatches(state.LocalContentHash, state.RemoteContentHash);

            if (baselineDiverged)
            {
                return SyncDeleteDirection.None;
            }

            if (!localDeleted && !remoteDeleted && !localChanged && !remoteChanged)
            {
                return SyncDeleteDirection.None;
            }

            if (localDeleted && remoteDeleted)
            {
                return SyncDeleteDirection.None;
            }

            if (localDeleted && !remoteChanged && remote is not null)
            {
                return SyncDeleteDirection.Remote;
            }

            if (remoteDeleted && !localChanged && local is not null)
            {
                return SyncDeleteDirection.Local;
            }

            return SyncDeleteDirection.None;
        }

        private static bool HasMissingRemoteOnlyPlaceholder(
            SyncPair syncPair,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                return false;
            }

            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                if (IsOnlineOnlyPlaceholderState(state.Value)
                    && !localByPath.ContainsKey(state.Key)
                    && remoteByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsOnlineOnlyPlaceholderBaseline(SyncPair syncPair, SyncStateEntry state)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && IsOnlineOnlyPlaceholderState(state);
        }

        private static bool IsOnlineOnlyPlaceholderBaseline(
            SyncPair syncPair,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && IsOnlineOnlyPlaceholderState(baseline);
        }

        private static bool IsLocalOnlineOnlyPlaceholderBaseline(
            SyncPair syncPair,
            LocalFileSnapshot local,
            SyncStateEntry state)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder
                && IsOnlineOnlyPlaceholderBaseline(syncPair, state);
        }

        private static bool IsOnlineOnlyPlaceholderState(SyncStateEntry state)
        {
            return state.Kind == SyncEntryKind.File
                && (state.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                    || state.PlaceholderHydrationState == SyncPlaceholderHydrationState.Dehydrated)
                && state.PlaceholderIdentity is { Length: > 0 };
        }

        private static bool HasRemoteFileBaseline(SyncStateEntry state)
        {
            return state.Kind == SyncEntryKind.File
                && (!string.IsNullOrWhiteSpace(state.RemoteContentHash)
                    || !string.IsNullOrWhiteSpace(state.RemoteETag)
                    || state.RemoteFileId.HasValue);
        }

        private static bool IsOnlineOnlyPlaceholderState(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return (baseline.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                    || baseline.PlaceholderHydrationState == SyncPlaceholderHydrationState.Dehydrated)
                && baseline.HasPlaceholderIdentity;
        }

        private static bool HasRemoteFileBaseline(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return !string.IsNullOrWhiteSpace(baseline.RemoteContentHash)
                || !string.IsNullOrWhiteSpace(baseline.RemoteETag)
                || baseline.RemoteFileId.HasValue;
        }

        private static bool ContentMatches(string? left, string? right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static SyncRunOptions CloneAsFullScope(SyncRunOptions options)
        {
            return new SyncRunOptions
            {
                Scope = SyncRunScope.Full,
                DeleteRemotePermanently = options.DeleteRemotePermanently,
                MinimumLocalUploadAge = options.MinimumLocalUploadAge,
                MaximumLocalDeletesPerRun = options.MaximumLocalDeletesPerRun,
                MaximumRemoteDeletesPerRun = options.MaximumRemoteDeletesPerRun,
                MaximumStoredResultActivities = options.MaximumStoredResultActivities,
                InitialVirtualFilesPopulationQueueCapacity = options.InitialVirtualFilesPopulationQueueCapacity,
                InitialVirtualFilesStateBatchSize = options.InitialVirtualFilesStateBatchSize,
                InitialVirtualFilesPlaceholderConcurrency = options.InitialVirtualFilesPlaceholderConcurrency,
                InitialVirtualFilesPlaceholderBatchSize = options.InitialVirtualFilesPlaceholderBatchSize,
                ActivityProgress = options.ActivityProgress,
                TransferProgress = options.TransferProgress,
                RunProgress = options.RunProgress,
                CooperativeYieldAsync = options.CooperativeYieldAsync,
            };
        }

        private static bool ShouldDeferLocalUpload(
            LocalFileSnapshot local,
            SyncRunOptions options,
            out TimeSpan remainingQuietTime)
        {
            remainingQuietTime = TimeSpan.Zero;
            if (options.MinimumLocalUploadAge <= TimeSpan.Zero)
            {
                return false;
            }

            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan age = nowUtc - local.LastWriteUtc.ToUniversalTime();
            if (age >= options.MinimumLocalUploadAge)
            {
                return false;
            }

            remainingQuietTime = options.MinimumLocalUploadAge - age;
            return true;
        }

        private static void ReportDeferredLocalUpload(
            SyncRunResult result,
            SyncRunOptions options,
            string relativePath,
            TimeSpan remainingQuietTime)
        {
            result.RecordDeferredLocalPath(relativePath);
            string details = "Local file is still changing; retry after "
                + FormatQuietTime(remainingQuietTime)
                + " quiet window.";
            Report(result, options, SyncActivityKind.Skipped, relativePath, details);
        }

        private static string FormatQuietTime(TimeSpan value)
        {
            if (value.TotalMilliseconds < 1000)
            {
                return Math.Ceiling(value.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "ms";
            }

            return value.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + "s";
        }

        private static bool IsRemotePreconditionFailed(HttpRequestException exception)
        {
            return exception.StatusCode == HttpStatusCode.PreconditionFailed;
        }

        private static Dictionary<string, T> ToDictionary<T>(IEnumerable<T> entries, Func<T, string> pathSelector)
        {
            var result = new Dictionary<string, T>(PathComparer);
            foreach (T entry in entries)
            {
                string relativePath = SyncPath.Normalize(pathSelector(entry));
                if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
                {
                    continue;
                }

                string key = SyncPath.ToKey(relativePath);
                if (result.TryGetValue(key, out T? existing))
                {
                    throw new SyncPathCollisionException(pathSelector(existing), relativePath);
                }

                NormalizeSnapshotPath(entry, relativePath);
                result[key] = entry;
            }

            return result;
        }

        private static void NormalizeSnapshotPath<T>(T entry, string relativePath)
        {
            switch (entry)
            {
                case LocalDirectorySnapshot directory:
                    directory.RelativePath = relativePath;
                    break;
                case LocalFileSnapshot file:
                    file.RelativePath = relativePath;
                    break;
                case RemoteDirectorySnapshot directory:
                    directory.RelativePath = relativePath;
                    break;
                case RemoteFileSnapshot file:
                    file.RelativePath = relativePath;
                    break;
            }
        }

        private static void ThrowIfPathKindCollisions<TLeft, TRight>(
            IReadOnlyDictionary<string, TLeft> left,
            IReadOnlyDictionary<string, TRight> right,
            Func<TLeft, string> leftPathSelector,
            Func<TRight, string> rightPathSelector)
        {
            foreach (KeyValuePair<string, TLeft> item in left)
            {
                if (right.TryGetValue(item.Key, out TRight? colliding))
                {
                    throw new SyncPathCollisionException(leftPathSelector(item.Value), rightPathSelector(colliding));
                }
            }
        }

        private static IReadOnlyList<string> BuildPathKeys(params IEnumerable<string>[] keySets)
        {
            List<string> keys = BuildUniquePathKeyList(keySets);
            keys.Sort(PathComparer.Compare);
            return keys;
        }

        private static IReadOnlyList<string> BuildDirectoryPathKeys(params IEnumerable<string>[] keySets)
        {
            List<string> keys = BuildUniquePathKeyList(keySets);
            keys.Sort(static (left, right) =>
            {
                int depthComparison = GetPathDepth(left).CompareTo(GetPathDepth(right));
                return depthComparison != 0
                    ? depthComparison
                    : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });
            return keys;
        }

        private static IReadOnlyList<string> BuildScopedRelativePaths(IEnumerable<string> relativePaths)
        {
            var yielded = new HashSet<string>(PathComparer);
            var paths = new List<string>();
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                if (yielded.Add(SyncPath.ToKey(normalizedPath)))
                {
                    paths.Add(normalizedPath);
                }
            }

            return paths;
        }

        private static IEnumerable<string> BuildScopedPathKeys(IEnumerable<string> relativePaths)
        {
            var yielded = new HashSet<string>(PathComparer);
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string current = string.Empty;
                for (int index = 0; index < segments.Length; index++)
                {
                    current = string.IsNullOrEmpty(current) ? segments[index] : current + "/" + segments[index];
                    string key = SyncPath.ToKey(current);
                    if (yielded.Add(key))
                    {
                        yield return key;
                    }
                }
            }
        }

        private static List<string> BuildUniquePathKeyList(params IEnumerable<string>[] keySets)
        {
            if (TryBuildSingleSourcePathKeyList(keySets, out List<string> singleSourceKeys))
            {
                return singleSourceKeys;
            }

            int initialCapacity = EstimateUniquePathKeyCapacity(keySets);
            var seen = new HashSet<string>(initialCapacity, PathComparer);
            var keys = new List<string>(initialCapacity);
            foreach (IEnumerable<string> keySet in keySets)
            {
                foreach (string key in keySet)
                {
                    if (seen.Add(key))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys;
        }

        private static bool TryBuildSingleSourcePathKeyList(IEnumerable<string>[] keySets, out List<string> keys)
        {
            IEnumerable<string>? singleSource = null;
            int singleSourceCount = 0;
            foreach (IEnumerable<string> keySet in keySets)
            {
                if (!keySet.TryGetNonEnumeratedCount(out int count))
                {
                    keys = [];
                    return false;
                }

                if (count == 0)
                {
                    continue;
                }

                if (singleSource is not null)
                {
                    keys = [];
                    return false;
                }

                singleSource = keySet;
                singleSourceCount = count;
            }

            keys = singleSource is null ? [] : new List<string>(singleSourceCount);
            if (singleSource is not null)
            {
                keys.AddRange(singleSource);
            }

            return true;
        }

        private static int EstimateUniquePathKeyCapacity(IEnumerable<string>[] keySets)
        {
            int capacity = 0;
            foreach (IEnumerable<string> keySet in keySets)
            {
                if (keySet.TryGetNonEnumeratedCount(out int count) && count > capacity)
                {
                    capacity = count;
                }
            }

            return capacity;
        }

        private static IEnumerable<string> EnumerateDirectoryDeleteKeys(IReadOnlyList<string> pathKeys)
        {
            for (int index = pathKeys.Count - 1; index >= 0;)
            {
                int depth = GetPathDepth(pathKeys[index]);
                int groupStart = index;
                while (groupStart > 0 && GetPathDepth(pathKeys[groupStart - 1]) == depth)
                {
                    groupStart--;
                }

                for (int groupIndex = groupStart; groupIndex <= index; groupIndex++)
                {
                    yield return pathKeys[groupIndex];
                }

                index = groupStart - 1;
            }
        }

        private static void Report(
            SyncRunResult result,
            SyncRunOptions options,
            SyncActivityKind kind,
            string relativePath,
            string? details,
            bool requiresUserAction = false,
            bool publishActivityProgress = true)
        {
            var activity = new SyncActivity
            {
                Kind = kind,
                RelativePath = SyncPath.Normalize(relativePath),
                Details = details,
                RequiresUserAction = requiresUserAction,
            };
            result.RecordActivity(activity, options.MaximumStoredResultActivities);
            if (publishActivityProgress)
            {
                options.ActivityProgress?.Report(activity);
            }
        }

        private static void ReportTransfer(
            SyncRunOptions options,
            SyncTransferDirection direction,
            string relativePath,
            long transferredBytes,
            long? totalBytes,
            bool isCompleted = false)
        {
            options.TransferProgress?.Report(new SyncTransferProgress(
                direction,
                relativePath,
                transferredBytes,
                totalBytes,
                isCompleted));
        }

        private async Task EnsureLocalContentHashAsync(
            LocalFileSnapshot local,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(local.ContentHash))
            {
                return;
            }

            if (_localContentHasher is null)
            {
                throw new InvalidOperationException("Local file snapshot does not include a content hash and no local content hasher is available.");
            }

            local.ContentHash = _localContentHashProgressHasher is null
                ? await _localContentHasher.ComputeContentHashAsync(local, cancellationToken).ConfigureAwait(false)
                : await _localContentHashProgressHasher
                    .ComputeContentHashAsync(local, options.TransferProgress, cancellationToken)
                    .ConfigureAwait(false);
        }

        private async Task EnsureLocalContentHashForBaselineComparisonAsync(
            LocalFileSnapshot local,
            SyncStateEntry state,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(local.ContentHash))
            {
                return;
            }

            if (local.IsCloudFilesOnlineOnlyPlaceholder && IsOnlineOnlyPlaceholderState(state))
            {
                local.ContentHash = !string.IsNullOrWhiteSpace(state.LocalContentHash)
                    ? state.LocalContentHash
                    : state.RemoteContentHash ?? string.Empty;
                return;
            }

            if (CanReuseBaselineLocalContentHash(local, state))
            {
                local.ContentHash = state.LocalContentHash!;
                return;
            }

            await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
        }

        private static bool CanReuseBaselineLocalContentHash(LocalFileSnapshot local, SyncStateEntry state)
        {
            return !string.IsNullOrWhiteSpace(state.LocalContentHash)
                && state.LocalSizeBytes.HasValue
                && state.LocalSizeBytes.Value == local.SizeBytes
                && state.LocalLastWriteUtc.HasValue
                && state.LocalLastWriteUtc.Value.ToUniversalTime() == local.LastWriteUtc.ToUniversalTime();
        }

        private static string ResolveUploadedLocalContentHash(LocalFileSnapshot local, NodeFileManifestDto uploaded)
        {
            if (!string.IsNullOrWhiteSpace(local.ContentHash))
            {
                return local.ContentHash;
            }

            if (!string.IsNullOrWhiteSpace(uploaded.ContentHash))
            {
                return uploaded.ContentHash;
            }

            throw new InvalidOperationException("Uploaded file manifest does not include a content hash.");
        }

        private readonly record struct MoveCandidateKey(string ContentHash, long SizeBytes);

        private readonly record struct RemoteDirectoryCreationResult(NodeDto Node, bool ReusedExisting);

        private sealed record InitialVirtualFilesStreamingPlan(
            bool SkipCurrentPlaceholders,
            IReadOnlyDictionary<string, InitialVirtualFilesPlaceholderBaseline> CurrentPlaceholderBaselineByPath);

        private readonly record struct InitialVirtualFilesPlaceholderBaseline(
            Guid? RemoteFileId,
            string? RemoteContentHash,
            string? RemoteETag,
            SyncPlaceholderHydrationState PlaceholderHydrationState,
            bool HasPlaceholderIdentity)
        {
            public static InitialVirtualFilesPlaceholderBaseline FromState(SyncStateEntry state)
            {
                return new InitialVirtualFilesPlaceholderBaseline(
                    state.RemoteFileId,
                    state.RemoteContentHash,
                    state.RemoteETag,
                    state.PlaceholderHydrationState,
                    state.PlaceholderIdentity is { Length: > 0 });
            }

            public static InitialVirtualFilesPlaceholderBaseline FromResumeEntry(
                SyncVirtualFilesResumeEntry entry)
            {
                return new InitialVirtualFilesPlaceholderBaseline(
                    entry.RemoteFileId,
                    entry.RemoteContentHash,
                    entry.RemoteETag,
                    entry.PlaceholderHydrationState,
                    entry.HasPlaceholderIdentity);
            }
        }

        private abstract record InitialVirtualFilesPopulationItem;

        private sealed record InitialVirtualFilesDirectoryPopulationItem(RemoteDirectorySnapshot Directory)
            : InitialVirtualFilesPopulationItem;

        private sealed record InitialVirtualFilesFilePopulationItem(RemoteFileSnapshot File)
            : InitialVirtualFilesPopulationItem;

        private sealed record InitialVirtualFilesFileWorkResult(
            string RelativePath,
            SyncStateEntry? State,
            SyncActivityKind ActivityKind,
            string? Details,
            bool RequiresUserAction,
            bool ReportActivity);

        private sealed class InitialVirtualFilesPopulationSink : IRemoteTreeStreamSink
        {
            private readonly ChannelWriter<InitialVirtualFilesPopulationItem> _writer;
            private readonly Action _onFileDiscovered;
            private readonly Action _onDirectoryDiscovered;

            public InitialVirtualFilesPopulationSink(
                ChannelWriter<InitialVirtualFilesPopulationItem> writer,
                Action onFileDiscovered,
                Action onDirectoryDiscovered)
            {
                _writer = writer ?? throw new ArgumentNullException(nameof(writer));
                _onFileDiscovered = onFileDiscovered ?? throw new ArgumentNullException(nameof(onFileDiscovered));
                _onDirectoryDiscovered = onDirectoryDiscovered ?? throw new ArgumentNullException(nameof(onDirectoryDiscovered));
            }

            public async ValueTask AddDirectoryAsync(
                RemoteDirectorySnapshot directory,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(directory);
                await _writer.WriteAsync(new InitialVirtualFilesDirectoryPopulationItem(directory), cancellationToken)
                    .ConfigureAwait(false);
                _onDirectoryDiscovered();
            }

            public async ValueTask AddFileAsync(RemoteFileSnapshot file, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(file);
                await _writer.WriteAsync(new InitialVirtualFilesFilePopulationItem(file), cancellationToken)
                    .ConfigureAwait(false);
                _onFileDiscovered();
            }
        }

        private sealed class RemoteTreeScanProgressCounter : IProgress<RemoteTreeScanProgress>
        {
            private int _pagesScanned;

            public int PagesScanned => Volatile.Read(ref _pagesScanned);

            public void Report(RemoteTreeScanProgress value)
            {
                ArgumentNullException.ThrowIfNull(value);
                int current;
                do
                {
                    current = Volatile.Read(ref _pagesScanned);
                    if (value.PagesScanned <= current)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref _pagesScanned, value.PagesScanned, current) != current);
            }
        }

        private sealed class InitialVirtualFilesRemoteProgressReporter : IProgress<RemoteTreeScanProgress>
        {
            private readonly IProgress<RemoteTreeScanProgress> _inner;
            private readonly SyncRunOptions _options;
            private readonly DateTime _startedAtUtc;
            private readonly Func<int> _getCompletedFiles;

            public InitialVirtualFilesRemoteProgressReporter(
                IProgress<RemoteTreeScanProgress> inner,
                SyncRunOptions options,
                DateTime startedAtUtc,
                Func<int> getCompletedFiles)
            {
                _inner = inner;
                _options = options;
                _startedAtUtc = startedAtUtc;
                _getCompletedFiles = getCompletedFiles;
            }

            public void Report(RemoteTreeScanProgress value)
            {
                ArgumentNullException.ThrowIfNull(value);
                _inner.Report(value);
                if (value.FilesScanned == 0)
                {
                    return;
                }

                int filesCompleted = _getCompletedFiles();
                int filesTotal = Math.Max(filesCompleted, value.FilesScanned);
                ReportRunProgress(
                    _options,
                    SyncRunProgressStage.CreatingPlaceholders,
                    filesCompleted,
                    filesTotal,
                    value.CurrentPath,
                    _startedAtUtc);
            }
        }

        private static void ReportRunProgress(
            SyncRunOptions options,
            SyncRunProgressStage stage,
            int filesCompleted,
            int? filesTotal,
            string? currentPath,
            DateTime startedAtUtc,
            bool isCompleted = false,
            long bytesCompleted = 0,
            long? bytesTotal = null)
        {
            SyncRunProgressReporter.Report(
                options,
                stage,
                filesCompleted,
                filesTotal,
                currentPath,
                startedAtUtc,
                isCompleted,
                bytesCompleted,
                bytesTotal);
        }

        private static void ReportItemRunProgress(
            SyncRunOptions options,
            SyncRunProgressStage stage,
            int itemsCompleted,
            int itemsTotal,
            string? currentPath,
            DateTime startedAtUtc,
            ref DateTime? lastReportedAtUtc,
            long bytesCompleted = 0,
            long? bytesTotal = null)
        {
            DateTime occurredAtUtc = DateTime.UtcNow;
            if (!ShouldReportItemRunProgress(itemsCompleted, itemsTotal, lastReportedAtUtc, occurredAtUtc))
            {
                return;
            }

            lastReportedAtUtc = occurredAtUtc;

            ReportRunProgress(
                options,
                stage,
                itemsCompleted,
                itemsTotal,
                currentPath,
                startedAtUtc,
                bytesCompleted: bytesCompleted,
                bytesTotal: bytesTotal);
        }

        private static bool ShouldReportItemRunProgress(
            int itemsCompleted,
            int itemsTotal,
            DateTime? lastReportedAtUtc,
            DateTime occurredAtUtc)
        {
            int itemInterval = GetRunProgressReportItemInterval(itemsTotal);
            return itemsTotal <= itemInterval
                || itemsCompleted == 0
                || itemsCompleted == itemsTotal
                || itemsCompleted % itemInterval == 0
                || (lastReportedAtUtc.HasValue
                    && occurredAtUtc - lastReportedAtUtc.Value >= RunProgressReportTimeInterval);
        }

        private static int GetRunProgressReportItemInterval(int itemsTotal)
        {
            return itemsTotal <= RunProgressDetailedItemLimit
                ? RunProgressDetailedItemInterval
                : RunProgressSparseItemInterval;
        }

        private static async ValueTask YieldAfterLargeBatchAsync(
            SyncRunOptions options,
            int itemsCompleted,
            int itemsTotal,
            CancellationToken cancellationToken)
        {
            int itemInterval = GetRunProgressReportItemInterval(itemsTotal);
            if (itemsTotal <= itemInterval
                || itemsCompleted <= 0
                || itemsCompleted >= itemsTotal
                || itemsCompleted % itemInterval != 0)
            {
                return;
            }

            if (options.CooperativeYieldAsync is { } cooperativeYieldAsync)
            {
                await cooperativeYieldAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
