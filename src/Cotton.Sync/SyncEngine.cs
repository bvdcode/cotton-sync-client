// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

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
        private static readonly TimeSpan InitialVirtualFilesHeartbeatLogInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RunProgressReportTimeInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan CloudFilesMetadataTimestampTolerance = TimeSpan.FromSeconds(2);
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private readonly ILocalFileScanner _localScanner;
        private readonly ILocalFileContentHasher? _localContentHasher;
        private readonly ILocalFileContentHashProgressHasher? _localContentHashProgressHasher;
        private readonly ILocalFileMetadataTreeScanner? _localMetadataTreeScanner;
        private readonly ILocalFileMetadataTreeLookupScanner? _localMetadataTreeLookupScanner;
        private readonly ILocalFileMetadataPathLookupScanner? _localMetadataPathLookupScanner;
        private readonly ILocalFilePresenceProbe? _localFilePresenceProbe;
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
        private readonly IRemoteFileMaterializationObserver? _remoteFileMaterializationObserver;
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
            _localFilePresenceProbe = localScanner as ILocalFilePresenceProbe;
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
            _remoteFileMaterializationObserver =
                remoteFilePlaceholderWriter as IRemoteFileMaterializationObserver;
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

            SyncRunOptions runOptions = options ?? new SyncRunOptions();
            ValidateOptions(runOptions);
            DateTime startedAtUtc = DateTime.UtcNow;
            bool initialWindowsVirtualFilesStreamingCanApply =
                CanRunInitialWindowsVirtualFilesStreaming(syncPair, runOptions);
            if (!initialWindowsVirtualFilesStreamingCanApply)
            {
                ReportRunProgress(runOptions, SyncRunProgressStage.ScanningLocal, 0, null, null, startedAtUtc);
            }

            _logger.LogInformation("Starting sync pass for pair {SyncPairId}.", syncPair.SyncPairId);
            await _stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncRunResult? initialVirtualFilesResult = await TryRunInitialWindowsVirtualFilesStreamingPopulationAsync(
                    syncPair,
                    runOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (initialVirtualFilesResult is not null)
            {
                LogInitialVirtualFilesPopulationResult(syncPair, initialVirtualFilesResult);
                return initialVirtualFilesResult;
            }

            if (initialWindowsVirtualFilesStreamingCanApply)
            {
                ReportRunProgress(runOptions, SyncRunProgressStage.ScanningLocal, 0, null, null, startedAtUtc);
            }

            SyncRunContext context = await PrepareSyncRunContextAsync(
                    syncPair,
                    runOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> directoryPathKeys = await ReconcileSyncDirectoriesAsync(context).ConfigureAwait(false);
            SyncDeletePlan deletePlan = await BuildSyncDeletePlanAsync(context).ConfigureAwait(false);
            await ReconcilePlannedDirectoryDeletesAsync(context, deletePlan, directoryPathKeys).ConfigureAwait(false);
            SyncFilePhaseResult filePhase = await ReconcileSyncFilesAsync(context, deletePlan).ConfigureAwait(false);
            await CompleteSyncRunAsync(context, deletePlan, directoryPathKeys, filePhase).ConfigureAwait(false);
            return context.Result;
        }

        private void LogInitialVirtualFilesPopulationResult(SyncPair syncPair, SyncRunResult result)
        {
            if (result.RequiresUserAction)
            {
                _logger.LogInformation(
                    "Windows virtual-files placeholder work for pair {SyncPairId} requires user action: {ActionRequiredMessage}.",
                    syncPair.SyncPairId,
                    result.ActionRequiredMessage);
                return;
            }

            _logger.LogInformation(
                "Completed sync pass for pair {SyncPairId} with Windows virtual-files placeholder work: {ActivityCount} activities, 0 file content transfers.",
                syncPair.SyncPairId,
                result.TotalActivityCount);
        }

        private async Task<SyncRunContext> PrepareSyncRunContextAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            SyncTreeLookups treeLookups = await ScanTreesAndBuildLookupsAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            (Dictionary<string, SyncStateEntry> directoryStateByPath, Dictionary<string, SyncStateEntry> fileStateByPath) =
                await LoadStateByPathAsync(syncPair.SyncPairId, options, treeLookups, cancellationToken)
                    .ConfigureAwait(false);
            ScopedVirtualFilesDirectoryRenamePlan? scopedDirectoryRename =
                await ExpandScopedVirtualFilesDirectoryRenameLookupsAsync(
                        syncPair,
                        options,
                        treeLookups,
                        directoryStateByPath,
                        fileStateByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateSyncTreePathKinds(treeLookups);
            return new SyncRunContext(
                syncPair,
                options,
                new SyncRunResult(),
                treeLookups,
                directoryStateByPath,
                fileStateByPath,
                scopedDirectoryRename,
                startedAtUtc,
                cancellationToken);
        }

        private static void ValidateSyncTreePathKinds(SyncTreeLookups treeLookups)
        {
            ThrowIfPathKindCollisions(
                treeLookups.LocalDirectoriesByPath,
                treeLookups.LocalFilesByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
            ThrowIfPathKindCollisions(
                treeLookups.RemoteDirectoriesByPath,
                treeLookups.RemoteFilesByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
        }

        private async Task<IReadOnlyList<string>> ReconcileSyncDirectoriesAsync(SyncRunContext context)
        {
            await CoalesceRemoteDirectoryMovesAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalDirectoriesByPath,
                    context.RemoteDirectoriesByPath,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.DirectoryStateByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> directoryPathKeys = BuildDirectoryPathKeys(
                context.LocalDirectoriesByPath.Keys,
                context.RemoteDirectoriesByPath.Keys,
                context.DirectoryStateByPath.Keys);
            ReportRunProgress(
                context.Options,
                SyncRunProgressStage.ReconcilingDirectories,
                0,
                directoryPathKeys.Count,
                null,
                context.StartedAtUtc);
            DirectoryReconciliationContext directoryReconciliation = new(
                context.SyncPair,
                context.Options,
                context.Result,
                directoryPathKeys,
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath,
                context.TreeLookups.RemoteRootNode,
                context.StartedAtUtc,
                context.CancellationToken);
            await ReconcileDirectoriesWithoutBaselineAsync(directoryReconciliation).ConfigureAwait(false);
            return directoryPathKeys;
        }

        private async Task<SyncDeletePlan> BuildSyncDeletePlanAsync(SyncRunContext context)
        {
            await EnsureLocalContentHashesForStateFilesAsync(
                    context.LocalFilesByPath,
                    context.FileStateByPath,
                    context.Options,
                    context.StartedAtUtc,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await CoalesceLocalOnlineOnlyPlaceholderMovesAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await CoalesceLocalFileMovesAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            if (context.ScopedDirectoryRename is not null)
            {
                await DeleteConfirmedScopedVirtualFilesDirectoryRenameSourceAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.ScopedDirectoryRename,
                        context.RemoteDirectoriesByPath,
                        context.RemoteFilesByPath,
                        context.DirectoryStateByPath,
                        context.FileStateByPath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            bool hasLocalDirectoryDeleteCandidates = HasLocalDirectoryDeleteCandidates(
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath);
            bool hasRemoteDirectoryDeleteCandidates = HasRemoteDirectoryDeleteCandidates(
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath);
            bool hasStaleDirectoryState = HasStaleDirectoryState(
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath);
            DirectoryContentIndex localDirectoryContentIndex = hasLocalDirectoryDeleteCandidates
                ? DirectoryContentIndex.Create(context.LocalDirectoriesByPath.Keys, context.LocalFilesByPath.Keys)
                : DirectoryContentIndex.Empty;
            DirectoryContentIndex remoteDirectoryContentIndex = hasRemoteDirectoryDeleteCandidates
                ? DirectoryContentIndex.Create(context.RemoteDirectoriesByPath.Keys, context.RemoteFilesByPath.Keys)
                : DirectoryContentIndex.Empty;
            ScopedVirtualFilesDirectoryDeletePlan? scopedDirectoryDelete =
                BuildConfirmedScopedVirtualFilesDirectoryDeletePlan(
                    context.SyncPair,
                    context.Options,
                    new ScopedVirtualFilesDirectoryDeleteContext(
                        context.LocalDirectoriesByPath,
                        context.RemoteDirectoriesByPath,
                        context.LocalFilesByPath,
                        context.RemoteFilesByPath,
                        context.DirectoryStateByPath,
                        context.FileStateByPath));
            IReadOnlySet<string>? scopedFileDeleteKeys = context.Options.Scope.IsFull
                ? null
                : BuildExactScopedPathKeys(context.Options.Scope.LocalChangedPaths);
            IReadOnlySet<string>? scopedDirectoryDeleteKeys = context.Options.Scope.IsFull
                ? null
                : BuildExactScopedPathKeys(context.Options.Scope.LocalChangedPaths);
            IReadOnlySet<string> scopedLocalDeletedFileKeys =
                BuildExactScopedPathKeys(context.Options.Scope.LocalDeletedPaths);
            if (scopedDirectoryDelete is not null)
            {
                scopedFileDeleteKeys = AddScopedPathKeys(scopedFileDeleteKeys!, scopedDirectoryDelete.FileKeys);
                scopedLocalDeletedFileKeys = AddScopedPathKeys(
                    scopedLocalDeletedFileKeys,
                    scopedDirectoryDelete.FileKeys);
            }
            SyncDeleteGuard deleteGuard = BuildDeleteGuard(
                context.Options,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath,
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex,
                scopedFileDeleteKeys,
                scopedDirectoryDeleteKeys,
                scopedLocalDeletedFileKeys,
                scopedDirectoryDelete);
            bool hasMissingRemoteOnlyPlaceholder = HasMissingRemoteOnlyPlaceholder(
                context.SyncPair,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            return new SyncDeletePlan(
                deleteGuard,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex,
                scopedFileDeleteKeys,
                scopedDirectoryDeleteKeys,
                scopedLocalDeletedFileKeys,
                scopedDirectoryDelete,
                hasLocalDirectoryDeleteCandidates,
                hasLocalDirectoryDeleteCandidates || hasRemoteDirectoryDeleteCandidates || hasStaleDirectoryState,
                hasMissingRemoteOnlyPlaceholder);
        }

        private async Task ReconcilePlannedDirectoryDeletesAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            IReadOnlyList<string> directoryPathKeys)
        {
            if (!deletePlan.RequiresDirectoryReconciliation)
            {
                return;
            }

            DirectoryDeleteContext directoryDeletes = new(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    deletePlan.DeleteGuard,
                    directoryPathKeys,
                    context.LocalDirectoriesByPath,
                    context.RemoteDirectoriesByPath,
                    context.DirectoryStateByPath,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    deletePlan.LocalDirectoryContentIndex,
                    deletePlan.RemoteDirectoryContentIndex,
                    deletePlan.ScopedDirectoryDeleteKeys,
                    deletePlan.ScopedDirectoryDelete?.DirectoryKeys,
                    context.CancellationToken);
            await ReconcileDirectoryDeletesAsync(directoryDeletes).ConfigureAwait(false);
        }

        private async Task<SyncFilePhaseResult> ReconcileSyncFilesAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan)
        {
            IReadOnlyList<string> pathKeys = BuildPathKeys(
                context.LocalFilesByPath.Keys,
                context.RemoteFilesByPath.Keys,
                context.FileStateByPath.Keys);
            EnsureEnoughLocalFreeSpaceForPlannedDownloads(
                context.SyncPair,
                pathKeys,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            long plannedTransferBytesTotal = CalculatePlannedTransferBytesTotal(
                context.SyncPair,
                pathKeys,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            SyncFileReconciliationProgress progress = new(plannedTransferBytesTotal);
            ReportRunProgress(
                context.Options,
                SyncRunProgressStage.ReconcilingFiles,
                progress.FilesCompleted,
                pathKeys.Count,
                null,
                context.StartedAtUtc,
                bytesCompleted: progress.CompletedTransferBytes,
                bytesTotal: plannedTransferBytesTotal);
            foreach (string key in pathKeys)
            {
                await ReconcileSyncFileAsync(context, deletePlan, progress, pathKeys.Count, key)
                    .ConfigureAwait(false);
            }

            return new SyncFilePhaseResult(pathKeys, progress.FilesCompleted, plannedTransferBytesTotal);
        }

        private async Task ReconcileSyncFileAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            SyncFileReconciliationProgress progress,
            int fileCount,
            string pathKey)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.LocalFilesByPath.TryGetValue(pathKey, out LocalFileSnapshot? local);
            context.RemoteFilesByPath.TryGetValue(pathKey, out RemoteFileSnapshot? remote);
            context.FileStateByPath.TryGetValue(pathKey, out SyncStateEntry? state);
            string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state?.RelativePath ?? pathKey;
            SyncRunProgressStage progressStage = ResolveFileRunProgressStage(context.SyncPair, local, remote, state);
            long plannedTransferBytes = CalculatePlannedTransferBytes(
                context.SyncPair,
                pathKey,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            ReportSyncFileProgress(context, progress, progressStage, fileCount, relativePath);
            if (state is null)
            {
                await ReconcileWithoutBaselineAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        relativePath,
                        local,
                        remote,
                        deletePlan.HasMissingRemoteOnlyPlaceholder,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReconcileWithBaselineAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        deletePlan.DeleteGuard,
                        deletePlan.ScopedFileDeleteKeys,
                        deletePlan.ScopedLocalDeletedFileKeys,
                        state,
                        relativePath,
                        local,
                        remote,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            progress.CompleteFile(plannedTransferBytes);
            ReportSyncFileProgress(context, progress, progressStage, fileCount, relativePath);
            await YieldAfterLargeBatchAsync(
                    context.Options,
                    progress.FilesCompleted,
                    fileCount,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static void ReportSyncFileProgress(
            SyncRunContext context,
            SyncFileReconciliationProgress progress,
            SyncRunProgressStage stage,
            int fileCount,
            string relativePath)
        {
            DateTime? lastReportedAtUtc = progress.LastReportedAtUtc;
            ReportItemRunProgress(
                context.Options,
                stage,
                progress.FilesCompleted,
                fileCount,
                relativePath,
                context.StartedAtUtc,
                ref lastReportedAtUtc,
                bytesCompleted: progress.CompletedTransferBytes,
                bytesTotal: progress.PlannedTransferBytesTotal);
            progress.LastReportedAtUtc = lastReportedAtUtc;
        }

        private async Task CompleteSyncRunAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            IReadOnlyList<string> directoryPathKeys,
            SyncFilePhaseResult filePhase)
        {
            if (deletePlan.HasLocalDirectoryDeleteCandidates)
            {
                await ReconcileEmptyLocalDirectoriesAfterFileDeletesAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        deletePlan.DeleteGuard,
                        directoryPathKeys,
                        context.LocalDirectoriesByPath,
                        context.RemoteDirectoriesByPath,
                        context.DirectoryStateByPath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (deletePlan.ScopedDirectoryDelete is not null)
            {
                await DeleteConfirmedScopedVirtualFilesDirectorySubtreesAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        deletePlan.DeleteGuard,
                        deletePlan.ScopedDirectoryDelete,
                        context.RemoteDirectoriesByPath,
                        context.DirectoryStateByPath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            ReportRunProgress(
                context.Options,
                SyncRunProgressStage.Completed,
                filePhase.FilesCompleted,
                filePhase.PathKeys.Count,
                null,
                context.StartedAtUtc,
                isCompleted: true,
                bytesCompleted: filePhase.PlannedTransferBytesTotal,
                bytesTotal: filePhase.PlannedTransferBytesTotal);
            _logger.LogInformation(
                "Completed sync pass for pair {SyncPairId} with {ActivityCount} activities.",
                context.SyncPair.SyncPairId,
                context.Result.TotalActivityCount);
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
            if (_localMetadataPathLookupScanner is null || _localContentHasher is null || _remotePathLookupCrawler is null)
            {
                throw new InvalidOperationException(
                    "Scoped sync requires local path lookup, local content hashing, and remote path lookup capabilities.");
            }

            IReadOnlyList<string> scopedPaths = BuildScopedRelativePaths(options.Scope.LocalChangedPaths);
            ReportRunProgress(options, SyncRunProgressStage.ScanningLocal, 0, scopedPaths.Count, null, startedAtUtc);
            LocalTreeLookupSnapshot localTreeLookups = await _localMetadataPathLookupScanner
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    scopedPaths,
                    new LocalTreeScanProgressReporter(options, startedAtUtc),
                    ShouldIncludeScopedDirectoryDescendants(syncPair),
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

        private bool CanExpandScopedVirtualFilesDirectoryRename(SyncPair syncPair, SyncRunOptions options)
        {
            return !options.Scope.IsFull
                && syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && _localMetadataPathLookupScanner is not null
                && _remotePathLookupCrawler is not null;
        }

        private static ScopedVirtualFilesDirectoryRenameCandidate? TryCreateScopedDirectoryRenameCandidate(
            IReadOnlySet<string> scopedKeys,
            SyncTreeLookups treeLookups,
            IDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            List<KeyValuePair<string, SyncStateEntry>> sourceDirectories = directoryStateByPath
                .Where(state =>
                    scopedKeys.Contains(state.Key)
                    && !treeLookups.LocalDirectoriesByPath.ContainsKey(state.Key)
                    && treeLookups.RemoteDirectoriesByPath.TryGetValue(state.Key, out RemoteDirectorySnapshot? remote)
                    && state.Value.RemoteNodeId == remote.Node.Id)
                .ToList();
            List<KeyValuePair<string, LocalDirectorySnapshot>> targetDirectories = treeLookups.LocalDirectoriesByPath
                .Where(local =>
                    scopedKeys.Contains(local.Key)
                    && !treeLookups.RemoteDirectoriesByPath.ContainsKey(local.Key)
                    && !directoryStateByPath.ContainsKey(local.Key))
                .ToList();
            List<KeyValuePair<string, SyncStateEntry>> sourceRootCandidates = sourceDirectories
                .Where(candidate => sourceDirectories.All(item => IsSameOrDescendantPathKey(item.Key, candidate.Key)))
                .ToList();
            List<KeyValuePair<string, LocalDirectorySnapshot>> targetRootCandidates = targetDirectories
                .Where(candidate => targetDirectories.All(item => IsSameOrDescendantPathKey(item.Key, candidate.Key)))
                .ToList();
            if (sourceRootCandidates.Count != 1 || targetRootCandidates.Count != 1)
            {
                return null;
            }

            KeyValuePair<string, SyncStateEntry> source = sourceRootCandidates[0];
            KeyValuePair<string, LocalDirectorySnapshot> target = targetRootCandidates[0];
            bool hasUnrelatedScopedPath = scopedKeys.Any(key =>
                !IsSameOrDescendantPathKey(key, source.Key)
                && !IsSameOrDescendantPathKey(source.Key, key)
                && !IsSameOrDescendantPathKey(key, target.Key)
                && !IsSameOrDescendantPathKey(target.Key, key));
            if (hasUnrelatedScopedPath)
            {
                return null;
            }

            return new ScopedVirtualFilesDirectoryRenameCandidate(
                source.Key,
                SyncPath.Normalize(source.Value.RelativePath),
                target.Key,
                SyncPath.Normalize(target.Value.RelativePath));
        }

        private async Task<List<SyncStateEntry>> LoadScopedRenameDescendantStatesAsync(
            string syncPairId,
            ScopedVirtualFilesDirectoryRenameCandidate candidate,
            CancellationToken cancellationToken)
        {
            List<SyncStateEntry> descendantStates = [];
            await foreach (SyncStateEntry state in _stateStore
                               .LoadEntriesByPathPrefixAsync(syncPairId, candidate.SourcePath, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        SyncPath.ToKey(state.RelativePath),
                        candidate.SourceKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    descendantStates.Add(state);
                }
            }

            return descendantStates;
        }

        private async Task<bool> HasStateAtPathPrefixAsync(
            string syncPairId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            await foreach (SyncStateEntry _ in _stateStore
                               .LoadEntriesByPathPrefixAsync(syncPairId, relativePath, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }

        private async Task<ScopedVirtualFilesDirectoryRenamePlan?> ExpandScopedVirtualFilesDirectoryRenameLookupsAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncTreeLookups treeLookups,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            if (!CanExpandScopedVirtualFilesDirectoryRename(syncPair, options))
            {
                return null;
            }

            HashSet<string> scopedKeys = options.Scope.LocalChangedPaths
                .Select(SyncPath.ToKey)
                .ToHashSet(PathComparer);
            ScopedVirtualFilesDirectoryRenameCandidate? candidate = TryCreateScopedDirectoryRenameCandidate(
                scopedKeys,
                treeLookups,
                directoryStateByPath);
            if (candidate is null)
            {
                return null;
            }

            List<SyncStateEntry> descendantStates = await LoadScopedRenameDescendantStatesAsync(
                    syncPair.SyncPairId,
                    candidate,
                    cancellationToken)
                .ConfigureAwait(false);
            if (descendantStates.Count == 0
                || await HasStateAtPathPrefixAsync(syncPair.SyncPairId, candidate.TargetPath, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }

            ScopedVirtualFilesDirectoryRenameValidation validation = await ScanScopedDirectoryRenameValidationAsync(
                    syncPair,
                    candidate,
                    descendantStates,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsScopedDirectoryRenameShapeConfirmed(candidate, validation)
                || !AreScopedDirectoryRenameStatesConfirmed(descendantStates, validation))
            {
                return null;
            }

            MergeScopedDirectoryRenameLookups(treeLookups, validation);
            MergeScopedDirectoryRenameState(directoryStateByPath, fileStateByPath, descendantStates);
            string[] sourceDirectoryKeys = [
                candidate.SourceKey,
                .. validation.ExpectedSourceDirectoryKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase),
            ];
            return new ScopedVirtualFilesDirectoryRenamePlan(
                candidate.SourcePath,
                sourceDirectoryKeys,
                validation.ExpectedSourceFileKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private async Task<ScopedVirtualFilesDirectoryRenameValidation> ScanScopedDirectoryRenameValidationAsync(
            SyncPair syncPair,
            ScopedVirtualFilesDirectoryRenameCandidate candidate,
            IReadOnlyList<SyncStateEntry> descendantStates,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string> targetPathBySourceKey = descendantStates.ToDictionary(
                state => SyncPath.ToKey(state.RelativePath),
                state => candidate.TargetPath + SyncPath.Normalize(state.RelativePath)[candidate.SourcePath.Length..],
                PathComparer);
            LocalTreeLookupSnapshot localDescendants = await _localMetadataPathLookupScanner!
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    targetPathBySourceKey.Values.ToArray(),
                    progress: null,
                    includeDirectoryDescendants: false,
                    cancellationToken)
                .ConfigureAwait(false);
            RemoteTreeLookupSnapshot remoteDescendants = await _remotePathLookupCrawler!
                .CrawlPathLookupsAsync(
                    syncPair.RemoteRootNodeId,
                    [candidate.SourcePath],
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            HashSet<string> expectedSourceDirectoryKeys = descendantStates
                .Where(static state => state.Kind == SyncEntryKind.Directory)
                .Select(state => SyncPath.ToKey(state.RelativePath))
                .ToHashSet(PathComparer);
            HashSet<string> expectedSourceFileKeys = descendantStates
                .Where(static state => state.Kind == SyncEntryKind.File)
                .Select(state => SyncPath.ToKey(state.RelativePath))
                .ToHashSet(PathComparer);
            return new ScopedVirtualFilesDirectoryRenameValidation(
                targetPathBySourceKey,
                expectedSourceDirectoryKeys,
                expectedSourceFileKeys,
                localDescendants,
                remoteDescendants);
        }

        private static bool IsScopedDirectoryRenameShapeConfirmed(
            ScopedVirtualFilesDirectoryRenameCandidate candidate,
            ScopedVirtualFilesDirectoryRenameValidation validation)
        {
            string sourcePrefix = candidate.SourceKey.TrimEnd('/') + "/";
            string targetPrefix = candidate.TargetKey.TrimEnd('/') + "/";
            HashSet<string> actualSourceDirectoryKeys = validation.RemoteDescendants.DirectoriesByPath.Keys
                .Where(key => key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> actualSourceFileKeys = validation.RemoteDescendants.FilesByPath.Keys
                .Where(key => key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> actualTargetDirectoryKeys = validation.LocalDescendants.DirectoriesByPath.Keys
                .Where(key => key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> actualTargetFileKeys = validation.LocalDescendants.FilesByPath.Keys
                .Where(key => key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(PathComparer);
            HashSet<string> expectedTargetDirectoryKeys = validation.ExpectedSourceDirectoryKeys
                .Select(key => SyncPath.ToKey(validation.TargetPathBySourceKey[key]))
                .ToHashSet(PathComparer);
            HashSet<string> expectedTargetFileKeys = validation.ExpectedSourceFileKeys
                .Select(key => SyncPath.ToKey(validation.TargetPathBySourceKey[key]))
                .ToHashSet(PathComparer);
            return validation.RemoteDescendants.DirectoriesByPath.ContainsKey(candidate.SourceKey)
                && actualSourceDirectoryKeys.SetEquals(validation.ExpectedSourceDirectoryKeys)
                && actualSourceFileKeys.SetEquals(validation.ExpectedSourceFileKeys)
                && actualTargetDirectoryKeys.SetEquals(expectedTargetDirectoryKeys)
                && actualTargetFileKeys.SetEquals(expectedTargetFileKeys);
        }

        private static bool AreScopedDirectoryRenameStatesConfirmed(
            IReadOnlyList<SyncStateEntry> descendantStates,
            ScopedVirtualFilesDirectoryRenameValidation validation)
        {
            foreach (SyncStateEntry state in descendantStates)
            {
                string stateKey = SyncPath.ToKey(state.RelativePath);
                string targetKey = SyncPath.ToKey(validation.TargetPathBySourceKey[stateKey]);
                bool isConfirmed = state.Kind switch
                {
                    SyncEntryKind.Directory =>
                        validation.LocalDescendants.DirectoriesByPath.ContainsKey(targetKey)
                        && validation.RemoteDescendants.DirectoriesByPath.TryGetValue(
                            stateKey,
                            out RemoteDirectorySnapshot? remote)
                        && state.RemoteNodeId == remote.Node.Id,
                    SyncEntryKind.File =>
                        validation.LocalDescendants.FilesByPath.ContainsKey(targetKey)
                        && validation.RemoteDescendants.FilesByPath.TryGetValue(
                            stateKey,
                            out RemoteFileSnapshot? remote)
                        && RemoteMatchesBaseline(remote.File, state),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(state),
                        state.Kind,
                        "Unknown sync state entry kind."),
                };
                if (!isConfirmed)
                {
                    return false;
                }
            }

            return true;
        }

        private static void MergeScopedDirectoryRenameLookups(
            SyncTreeLookups treeLookups,
            ScopedVirtualFilesDirectoryRenameValidation validation)
        {
            foreach (KeyValuePair<string, LocalDirectorySnapshot> local in validation.LocalDescendants.DirectoriesByPath)
            {
                treeLookups.LocalDirectoriesByPath[local.Key] = local.Value;
            }

            foreach (KeyValuePair<string, LocalFileSnapshot> local in validation.LocalDescendants.FilesByPath)
            {
                treeLookups.LocalFilesByPath[local.Key] = local.Value;
            }

            foreach (KeyValuePair<string, RemoteDirectorySnapshot> remote in validation.RemoteDescendants.DirectoriesByPath)
            {
                treeLookups.RemoteDirectoriesByPath[remote.Key] = remote.Value;
            }

            foreach (KeyValuePair<string, RemoteFileSnapshot> remote in validation.RemoteDescendants.FilesByPath)
            {
                treeLookups.RemoteFilesByPath[remote.Key] = remote.Value;
            }
        }

        private static void MergeScopedDirectoryRenameState(
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyList<SyncStateEntry> descendantStates)
        {
            foreach (SyncStateEntry state in descendantStates)
            {
                string key = SyncPath.ToKey(state.RelativePath);
                switch (state.Kind)
                {
                    case SyncEntryKind.Directory:
                        directoryStateByPath[key] = state;
                        break;
                    case SyncEntryKind.File:
                        fileStateByPath[key] = state;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(state),
                            state.Kind,
                            "Unknown sync state entry kind.");
                }
            }
        }

        private static ScopedVirtualFilesDirectoryDeletePlan? BuildConfirmedScopedVirtualFilesDirectoryDeletePlan(
            SyncPair syncPair,
            SyncRunOptions options,
            ScopedVirtualFilesDirectoryDeleteContext context)
        {
            if (options.Scope.IsFull
                || syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                return null;
            }

            IReadOnlySet<string> deletedKeys = BuildExactScopedPathKeys(options.Scope.LocalDeletedPaths);
            List<string> candidateKeys = context.DirectoryStateByPath.Keys
                .Where(key =>
                    deletedKeys.Contains(key)
                    && !context.LocalDirectoriesByPath.ContainsKey(key)
                    && context.RemoteDirectoriesByPath.ContainsKey(key))
                .ToList();
            string[] rootKeys = candidateKeys
                .Where(candidate => candidateKeys.All(other =>
                    PathComparer.Equals(candidate, other)
                    || !IsSameOrDescendantPathKey(candidate, other)))
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (rootKeys.Length == 0)
            {
                return null;
            }

            HashSet<string> directoryKeys = new(PathComparer);
            HashSet<string> fileKeys = new(PathComparer);
            List<string> rootPaths = [];
            foreach (string rootKey in rootKeys)
            {
                ScopedVirtualFilesDirectoryDeleteRoot? root = TryCreateConfirmedScopedDirectoryDeleteRoot(
                    context,
                    rootKey);
                if (root is null)
                {
                    return null;
                }

                rootPaths.Add(root.RelativePath);
                directoryKeys.UnionWith(root.DirectoryKeys);
                fileKeys.UnionWith(root.FileKeys);
            }

            string[] orderedFileKeys = fileKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
            return new ScopedVirtualFilesDirectoryDeletePlan(
                rootPaths,
                directoryKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                orderedFileKeys,
                orderedFileKeys.Select(key => context.FileStateByPath[key].RelativePath).ToArray());
        }

        private static ScopedVirtualFilesDirectoryDeleteRoot? TryCreateConfirmedScopedDirectoryDeleteRoot(
            ScopedVirtualFilesDirectoryDeleteContext context,
            string rootKey)
        {
            bool hasLocalSubtree = context.LocalDirectoriesByPath.Keys.Any(
                    key => IsSameOrDescendantPathKey(key, rootKey))
                || context.LocalFilesByPath.Keys.Any(key => IsSameOrDescendantPathKey(key, rootKey));
            if (hasLocalSubtree)
            {
                return null;
            }

            HashSet<string> expectedDirectoryKeys = context.DirectoryStateByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            HashSet<string> expectedFileKeys = context.FileStateByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            HashSet<string> actualDirectoryKeys = context.RemoteDirectoriesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            HashSet<string> actualFileKeys = context.RemoteFilesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, rootKey))
                .ToHashSet(PathComparer);
            if (!actualDirectoryKeys.SetEquals(expectedDirectoryKeys)
                || !actualFileKeys.SetEquals(expectedFileKeys)
                || !HaveMatchingScopedDeleteDirectoryIds(context, expectedDirectoryKeys)
                || !HaveMatchingScopedDeleteFileBaselines(context, expectedFileKeys))
            {
                return null;
            }

            return new ScopedVirtualFilesDirectoryDeleteRoot(
                context.DirectoryStateByPath[rootKey].RelativePath,
                expectedDirectoryKeys,
                expectedFileKeys);
        }

        private static bool HaveMatchingScopedDeleteDirectoryIds(
            ScopedVirtualFilesDirectoryDeleteContext context,
            IEnumerable<string> directoryKeys)
        {
            foreach (string key in directoryKeys)
            {
                if (context.DirectoryStateByPath[key].RemoteNodeId != context.RemoteDirectoriesByPath[key].Node.Id)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HaveMatchingScopedDeleteFileBaselines(
            ScopedVirtualFilesDirectoryDeleteContext context,
            IEnumerable<string> fileKeys)
        {
            foreach (string key in fileKeys)
            {
                SyncStateEntry state = context.FileStateByPath[key];
                RemoteFileSnapshot remote = context.RemoteFilesByPath[key];
                if (state.RemoteFileId != remote.File.Id || !RemoteMatchesBaseline(remote.File, state))
                {
                    return false;
                }
            }

            return true;
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
            InitialVirtualFilesStreamingPlanDecision streamingPlanDecision =
                await CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
                        syncPair,
                        options,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            InitialVirtualFilesStreamingPlan? streamingPlan = streamingPlanDecision.Plan;
            if (streamingPlan is null)
            {
                return null;
            }

            long startingManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            LogInitialVirtualFilesPopulationStarted(syncPair, options, startingManagedHeapBytes);
            Stopwatch stopwatch = Stopwatch.StartNew();
            SyncRunResult result = new();
            Channel<InitialVirtualFilesPopulationItem> channel = Channel.CreateBounded<InitialVirtualFilesPopulationItem>(
                new BoundedChannelOptions(options.InitialVirtualFilesPopulationQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
            InitialVirtualFilesPopulationMetrics metrics = new(startingManagedHeapBytes);
            InitialVirtualFilesRemoteProgressReporter initialVirtualFilesProgress = new(
                metrics.RemoteScanProgress,
                options,
                startedAtUtc,
                publishRunProgress: !streamingPlan.SkipCurrentPlaceholders,
                metrics);
            if (!streamingPlan.SkipCurrentPlaceholders)
            {
                ReportRunProgress(options, SyncRunProgressStage.CreatingPlaceholders, 0, null, null, startedAtUtc);
            }

            using IDisposable? providerWriteBurst = _remoteFilePlaceholderPopulationObserver
                ?.BeginPopulation(syncPair.SyncPairId, syncPair.LocalRootPath);
            using CancellationTokenSource streamingCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            InitialVirtualFilesPopulationSink sink = new(channel.Writer, metrics);
            InitialVirtualFilesPopulationContext context = new(
                syncPair,
                options,
                result,
                channel.Reader,
                startedAtUtc,
                streamingPlan,
                metrics,
                streamingCancellation.Token);
            Task producer = ProduceInitialWindowsVirtualFilesPopulationAsync(
                syncPair,
                options,
                startedAtUtc,
                channel,
                sink,
                initialVirtualFilesProgress,
                streamingCancellation.Token);
            Task consumer = ConsumeInitialWindowsVirtualFilesPopulationAsync(context);
            using CancellationTokenSource heartbeatCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task heartbeat = LogInitialVirtualFilesPopulationHeartbeatAsync(
                syncPair,
                options,
                stopwatch,
                metrics,
                heartbeatCancellation.Token);
            await RunInitialVirtualFilesPopulationPipelineAsync(
                    producer,
                    consumer,
                    heartbeat,
                    channel.Writer,
                    streamingCancellation,
                    heartbeatCancellation)
                .ConfigureAwait(false);

            stopwatch.Stop();
            CompleteInitialVirtualFilesPopulation(syncPair, options, result, startedAtUtc, streamingPlan, metrics, stopwatch);
            return result;
        }

        private void LogInitialVirtualFilesPopulationStarted(
            SyncPair syncPair,
            SyncRunOptions options,
            long startingManagedHeapBytes)
        {
            _logger.LogInformation(
                "Starting initial streaming Windows virtual-files population for pair {SyncPairId} with queue capacity {QueueCapacity}, placeholder concurrency {PlaceholderConcurrency}, placeholder batch size {PlaceholderBatchSize}, state batch size {StateBatchSize}, and managed heap {ManagedHeapBytes} bytes.",
                syncPair.SyncPairId,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize,
                startingManagedHeapBytes);
        }

        private static async Task RunInitialVirtualFilesPopulationPipelineAsync(
            Task producer,
            Task consumer,
            Task heartbeat,
            ChannelWriter<InitialVirtualFilesPopulationItem> writer,
            CancellationTokenSource streamingCancellation,
            CancellationTokenSource heartbeatCancellation)
        {
            try
            {
                Task firstCompleted = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
                if (firstCompleted.IsFaulted || firstCompleted.IsCanceled)
                {
                    await streamingCancellation.CancelAsync().ConfigureAwait(false);
                    writer.TryComplete(firstCompleted.Exception);
                }

                await Task.WhenAll(producer, consumer).ConfigureAwait(false);
            }
            finally
            {
                await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
                await IgnoreExpectedHeartbeatCancellationAsync(heartbeat, heartbeatCancellation.Token).ConfigureAwait(false);
            }
        }

        private void CompleteInitialVirtualFilesPopulation(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            DateTime startedAtUtc,
            InitialVirtualFilesStreamingPlan streamingPlan,
            InitialVirtualFilesPopulationMetrics metrics,
            Stopwatch stopwatch)
        {
            int completedItems = GetInitialVirtualFilesItemCount(metrics.CompletedFiles, metrics.CompletedDirectories);
            int discoveredItems = GetInitialVirtualFilesItemCount(metrics.DiscoveredFiles, metrics.DiscoveredDirectories);
            int totalItems = Math.Max(completedItems, discoveredItems);
            if (!streamingPlan.SkipCurrentPlaceholders || metrics.LastPlaceholderProgressReportedAtUtc.HasValue)
            {
                ReportRunProgress(
                    options,
                    SyncRunProgressStage.CreatingPlaceholders,
                    completedItems,
                    totalItems,
                    null,
                    startedAtUtc);
            }
            ReportRunProgress(
                options,
                SyncRunProgressStage.Completed,
                completedItems,
                totalItems,
                null,
                startedAtUtc,
                isCompleted: true);
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds <= 0d
                ? 1d
                : stopwatch.Elapsed.TotalSeconds;
            int finalDiscoveredDirectoryCount = metrics.DiscoveredDirectories;
            int finalDiscoveredFileCount = metrics.DiscoveredFiles;
            double discoveredDirectoryRatePerSecond = finalDiscoveredDirectoryCount / elapsedSeconds;
            double discoveredFileRatePerSecond = finalDiscoveredFileCount / elapsedSeconds;
            double createdPlaceholderRatePerSecond = metrics.CreatedPlaceholders / elapsedSeconds;
            double stateWriteRatePerSecond =
                (metrics.StateFileRowsWritten + metrics.StateDirectoryRowsWritten) / elapsedSeconds;
            RemoteTreeScanProgressCounter remoteScanProgress = metrics.RemoteScanProgress;
            int remotePageCount = remoteScanProgress.PagesScanned;
            double remotePageAverageLatencyMilliseconds = remotePageCount <= 0
                ? 0d
                : remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds / remotePageCount;
            long completedManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            metrics.RecordManagedHeapSample(completedManagedHeapBytes);
            _logger.LogInformation(
                "Completed initial streaming Windows virtual-files population for pair {SyncPairId} with {DirectoryCount} directories discovered at {DirectoryDiscoveryRatePerSecond:F2} dirs/sec, {FileCount} files discovered at {FileDiscoveryRatePerSecond:F2} files/sec, remote pages read={RemotePageCount}, remote page latency total={RemotePageLatencyTotalMilliseconds:F0} ms, avg={RemotePageLatencyAverageMilliseconds:F2} ms, max={RemotePageLatencyMaxMilliseconds:F0} ms, last={RemotePageLatencyLastMilliseconds:F0} ms, {CompletedFileCount} file items completed, {CreatedPlaceholderCount} placeholders created or refreshed, {SkippedCurrentPlaceholderCount} current placeholders skipped, {SkippedUnavailablePlaceholderCount} placeholders skipped with user action in {ElapsedMilliseconds} ms at {CreatedPlaceholderRatePerSecond:F2} placeholders/sec; state writes {StateFileRowsWritten} file rows, file write batches {StateFileWriteBatchCount}, directory rows {StateDirectoryRowsWritten}, state write rate={StateWriteRatePerSecond:F2} rows/sec; managed heap start={StartingManagedHeapBytes} bytes, completed={CompletedManagedHeapBytes} bytes, peak={PeakManagedHeapBytes} bytes, delta={ManagedHeapDeltaBytes} bytes; queue capacity={QueueCapacity}, placeholder concurrency={PlaceholderConcurrency}, placeholder batch size={PlaceholderBatchSize}, state batch size={StateBatchSize}; activities retained {RetainedActivityCount}/{TotalActivityCount}, truncated={ActivityListTruncated}.",
                syncPair.SyncPairId,
                finalDiscoveredDirectoryCount,
                discoveredDirectoryRatePerSecond,
                finalDiscoveredFileCount,
                discoveredFileRatePerSecond,
                remotePageCount,
                remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds,
                remotePageAverageLatencyMilliseconds,
                remoteScanProgress.PageReadLatencyMax.TotalMilliseconds,
                remoteScanProgress.LastPageReadLatency.TotalMilliseconds,
                metrics.CompletedFiles,
                metrics.CreatedPlaceholders,
                metrics.SkippedCurrentPlaceholders,
                metrics.SkippedUnavailablePlaceholders,
                stopwatch.ElapsedMilliseconds,
                createdPlaceholderRatePerSecond,
                metrics.StateFileRowsWritten,
                metrics.StateFileWriteBatches,
                metrics.StateDirectoryRowsWritten,
                stateWriteRatePerSecond,
                metrics.StartingManagedHeapBytes,
                completedManagedHeapBytes,
                metrics.PeakManagedHeapBytes,
                completedManagedHeapBytes - metrics.StartingManagedHeapBytes,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize,
                result.Activities.Count,
                result.TotalActivityCount,
                result.IsActivityListTruncated);
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision> CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (!CanRunInitialWindowsVirtualFilesStreaming(syncPair, options))
            {
                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            InitialVirtualFilesStreamingPlanDecision? stateFirstDecision =
                await TryCreateStateFirstWindowsVirtualFilesStreamingPlanAsync(syncPair, cancellationToken)
                    .ConfigureAwait(false);
            if (stateFirstDecision is not null)
            {
                return stateFirstDecision;
            }

            return await InspectLocalTreeForInitialWindowsVirtualFilesStreamingDecisionAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken).ConfigureAwait(false);
        }

        private bool CanRunInitialWindowsVirtualFilesStreaming(SyncPair syncPair, SyncRunOptions options)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && options.Scope.IsFull
                && options.AllowInitialVirtualFilesStreaming
                && _remoteStreamingCrawler is not null
                && _remoteFilePlaceholderWriter is not null;
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision?> TryCreateStateFirstWindowsVirtualFilesStreamingPlanAsync(
            SyncPair syncPair,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            var inspection = new InitialVirtualFilesStateFirstInspection();
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.SyncPairId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    await _stateStore.DeleteAsync(syncPair.SyncPairId, entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string? issue = inspection.Add(entry);
                if (issue is not null)
                {
                    LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, issue);
                    return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
                }
            }

            if (inspection.EntriesSeen == 0)
            {
                LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, "no persisted state entries");
                return null;
            }

            if (_localMetadataPathLookupScanner is null)
            {
                LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, "local path metadata lookup is unavailable");
                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            LocalTreeLookupSnapshot localStateLookups = await _localMetadataPathLookupScanner
                .ScanPathMetadataLookupsAsync(
                    syncPair.LocalRootPath,
                    inspection.StateRelativePaths,
                    progress: null,
                    includeDirectoryDescendants: false,
                    cancellationToken)
                .ConfigureAwait(false);
            string? incompatibility = inspection.FindLocalIncompatibility(localStateLookups);
            if (incompatibility is not null)
            {
                LogStateFirstPlanSkipped(syncPair, inspection, stopwatch, incompatibility);
                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Loaded Windows virtual-files state-first resume plan for pair {SyncPairId} with {DirectoryStateCount} directories and {FileStateCount} files ({OnlineOnlyFileStateCount} online-only, {MaterializedFileStateCount} materialized) in {ElapsedMilliseconds} ms without scanning the local placeholder tree.",
                syncPair.SyncPairId,
                inspection.DirectoryEntries,
                inspection.FileEntries,
                inspection.OnlineOnlyFileEntries,
                inspection.MaterializedFileEntries,
                stopwatch.ElapsedMilliseconds);
            return InitialVirtualFilesStreamingPlanDecision.FromPlan(
                new InitialVirtualFilesStreamingPlan(
                    SkipCurrentPlaceholders: true,
                    CurrentPlaceholderBaselineByPath: inspection.FileBaselineByPath,
                    AdoptableUntrackedPlaceholderByPath: new Dictionary<string, LocalFileSnapshot>(PathComparer)));
        }

        private void LogStateFirstPlanSkipped(
            SyncPair syncPair,
            InitialVirtualFilesStateFirstInspection inspection,
            Stopwatch stopwatch,
            string reason)
        {
            stopwatch.Stop();
            _logger.LogInformation(
                "Skipping Windows virtual-files state-first resume plan for pair {SyncPairId}: {Reason}. Entries seen={EntryStateCount}, directories={DirectoryStateCount}, files={FileStateCount}, online-only files={OnlineOnlyFileStateCount}, materialized files={MaterializedFileStateCount}, elapsed={ElapsedMilliseconds} ms.",
                syncPair.SyncPairId,
                reason,
                inspection.EntriesSeen,
                inspection.DirectoryEntries,
                inspection.FileEntries,
                inspection.OnlineOnlyFileEntries,
                inspection.MaterializedFileEntries,
                stopwatch.ElapsedMilliseconds);
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision> InspectLocalTreeForInitialWindowsVirtualFilesStreamingDecisionAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            SyncRunOptions silentPreflightOptions = CloneWithoutRunProgress(options);
            LocalTreeLookupSnapshot? localTreeLookups = await ScanLocalTreeLookupsAsync(
                    syncPair.LocalRootPath,
                    silentPreflightOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (localTreeLookups is not null)
            {
                return await CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
                        syncPair,
                        localTreeLookups.DirectoriesByPath,
                        localTreeLookups.FilesByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            LocalTreeSnapshot localTree = await ScanLocalTreeAsync(
                    syncPair.LocalRootPath,
                    silentPreflightOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            Dictionary<string, LocalDirectorySnapshot> directoriesByPath = localTree.Directories.ToDictionary(
                directory => SyncPath.ToKey(directory.RelativePath),
                PathComparer);
            Dictionary<string, LocalFileSnapshot> filesByPath = localTree.Files.ToDictionary(
                file => SyncPath.ToKey(file.RelativePath),
                PathComparer);
            return await CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
                    syncPair,
                    directoriesByPath,
                    filesByPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<InitialVirtualFilesStreamingPlanDecision> CreateInitialWindowsVirtualFilesStreamingPlanDecisionAsync(
            SyncPair syncPair,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, LocalFileSnapshot> localFilesByPath,
            CancellationToken cancellationToken)
        {
            if (localDirectoriesByPath.Count == 0 && localFilesByPath.Count == 0)
            {
                return InitialVirtualFilesStreamingPlanDecision.FromPlan(
                    new InitialVirtualFilesStreamingPlan(
                        SkipCurrentPlaceholders: false,
                        CurrentPlaceholderBaselineByPath: new Dictionary<string, InitialVirtualFilesPlaceholderBaseline>(PathComparer),
                        AdoptableUntrackedPlaceholderByPath: new Dictionary<string, LocalFileSnapshot>(PathComparer)));
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
            var adoptableUntrackedPlaceholderByPath = new Dictionary<string, LocalFileSnapshot>(PathComparer);
            foreach ((string fileKey, LocalFileSnapshot local) in localFilesByPath)
            {
                if (fileBaselineByPath.TryGetValue(fileKey, out InitialVirtualFilesPlaceholderBaseline baseline)
                    && IsResumeCompatibleVirtualFilesPlaceholder(local, baseline))
                {
                    continue;
                }

                if (IsUntrackedVirtualFilesPlaceholderCompatibleWithInitialStreaming(local))
                {
                    adoptableUntrackedPlaceholderByPath[fileKey] = local;
                    continue;
                }

                return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
            }

            foreach (string directoryKey in localDirectoriesByPath.Keys)
            {
                if (directoryStateByPath.TryGetValue(directoryKey, out Guid? remoteNodeId)
                    && remoteNodeId is null)
                {
                    return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
                }

                if (!directoryStateByPath.ContainsKey(directoryKey)
                    && !HasAdoptablePlaceholderDescendant(directoryKey, adoptableUntrackedPlaceholderByPath.Keys))
                {
                    return InitialVirtualFilesStreamingPlanDecision.NotApplicable;
                }
            }

            return InitialVirtualFilesStreamingPlanDecision.FromPlan(
                new InitialVirtualFilesStreamingPlan(
                    SkipCurrentPlaceholders: true,
                    CurrentPlaceholderBaselineByPath: fileBaselineByPath,
                    AdoptableUntrackedPlaceholderByPath: adoptableUntrackedPlaceholderByPath));
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

        private static bool HasAdoptablePlaceholderDescendant(
            string directoryKey,
            IEnumerable<string> adoptablePlaceholderPathKeys)
        {
            string directoryPrefix = directoryKey.TrimEnd('/') + "/";
            return adoptablePlaceholderPathKeys.Any(pathKey =>
                pathKey.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsResumeCompatibleVirtualFilesPlaceholder(
            LocalFileSnapshot local,
            InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsResumeCompatible(local, baseline);
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
            InitialVirtualFilesPopulationContext context)
        {
            int placeholderBatchSize = _remoteFilePlaceholderWriter is IRemoteFilePlaceholderBatchWriter
                ? context.Options.InitialVirtualFilesPlaceholderBatchSize
                : 1;
            InitialVirtualFilesConsumerState state = new(
                context.Options.InitialVirtualFilesStateBatchSize,
                placeholderBatchSize,
                context.Options.InitialVirtualFilesPlaceholderConcurrency,
                _remoteDirectoryTreePopulationObserver is not null,
                PathComparer);

            try
            {
                await foreach (InitialVirtualFilesPopulationItem item in context.Reader
                                   .ReadAllAsync(context.CancellationToken)
                                   .ConfigureAwait(false))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    switch (item)
                    {
                        case InitialVirtualFilesDirectoryPopulationItem directoryItem:
                            await ProcessInitialVirtualFilesDirectoryAsync(context, state, directoryItem.Directory)
                                .ConfigureAwait(false);
                            break;

                        case InitialVirtualFilesFilePopulationItem fileItem:
                            await ProcessInitialVirtualFilesFileAsync(context, state, fileItem.File)
                                .ConfigureAwait(false);
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"Unsupported initial virtual-files population item type '{item.GetType().FullName}'.");
                    }
                }

                await FinalizeInitialVirtualFilesPopulationAsync(context, state).ConfigureAwait(false);
            }
            finally
            {
                await FlushInitialVirtualFilesPopulationStateAsync(context, state).ConfigureAwait(false);
            }
        }

        private async Task ProcessInitialVirtualFilesDirectoryAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state,
            RemoteDirectorySnapshot directory)
        {
            EnqueueInitialVirtualFilesFileBatchWork(
                state.PendingFileTasks,
                state.PendingFileBatch,
                context.SyncPair,
                context.Options,
                context.CancellationToken);
            await DrainCompletedInitialVirtualFilesAsync(
                    state.PendingFileTasks,
                    state.PendingFileStates,
                    context,
                    waitForOne: false)
                .ConfigureAwait(false);
            await CreateRemoteBackedLocalDirectoryAsync(
                    context.SyncPair,
                    directory.RelativePath,
                    directory.Node,
                    context.CancellationToken)
                .ConfigureAwait(false);
            RecordInitialVirtualFilesDirectoryFinalization(context.SyncPair, state, directory);
            state.PendingDirectoryStates.Add(BuildDirectoryBaseline(
                context.SyncPair,
                directory.RelativePath,
                directory.Node));
            if (state.PendingDirectoryStates.Count >= context.Options.InitialVirtualFilesStateBatchSize)
            {
                int flushedDirectoryRows = await FlushInitialVirtualFilesStateBatchAsync(
                        state.PendingDirectoryStates,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                context.Metrics.RecordDirectoryStateWrite(flushedDirectoryRows);
            }

            context.Metrics.RecordCompletedDirectory();
            ReportInitialVirtualFilesStreamingProgress(context, directory.RelativePath);
        }

        private static void RecordInitialVirtualFilesDirectoryFinalization(
            SyncPair syncPair,
            InitialVirtualFilesConsumerState state,
            RemoteDirectorySnapshot directory)
        {
            if (state.DirectoryFinalizationRequests is null)
            {
                return;
            }

            RemoteDirectoryMaterializationRequest request = CreateRemoteDirectoryMaterializationRequest(
                syncPair,
                directory.RelativePath,
                directory.Node);
            state.DirectoryFinalizationRequests[SyncPath.ToKey(request.RelativePath)] = request;
        }

        private async Task ProcessInitialVirtualFilesFileAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state,
            RemoteFileSnapshot file)
        {
            state.StreamedRemoteFileKeys.Add(SyncPath.ToKey(file.RelativePath));
            InitialVirtualFilesFileWorkResult? currentPlaceholderWorkResult =
                TryCreateCurrentInitialVirtualFilesFileWorkResult(context.SyncPair, file, context.StreamingPlan);
            if (currentPlaceholderWorkResult is not null)
            {
                await CompleteInitialVirtualFilesFileWorkAsync(
                        currentPlaceholderWorkResult,
                        state.PendingFileStates,
                        context)
                    .ConfigureAwait(false);
                return;
            }

            state.PendingFileBatch.Add(file);
            int placeholderBatchSize = _remoteFilePlaceholderWriter is IRemoteFilePlaceholderBatchWriter
                ? context.Options.InitialVirtualFilesPlaceholderBatchSize
                : 1;
            if (state.PendingFileBatch.Count >= placeholderBatchSize)
            {
                EnqueueInitialVirtualFilesFileBatchWork(
                    state.PendingFileTasks,
                    state.PendingFileBatch,
                    context.SyncPair,
                    context.Options,
                    context.CancellationToken);
            }

            if (state.PendingFileTasks.Count >= context.Options.InitialVirtualFilesPlaceholderConcurrency)
            {
                await DrainCompletedInitialVirtualFilesAsync(
                        state.PendingFileTasks,
                        state.PendingFileStates,
                        context,
                        waitForOne: true)
                    .ConfigureAwait(false);
            }
        }

        private async Task FinalizeInitialVirtualFilesPopulationAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state)
        {
            EnqueueInitialVirtualFilesFileBatchWork(
                state.PendingFileTasks,
                state.PendingFileBatch,
                context.SyncPair,
                context.Options,
                context.CancellationToken);
            while (state.PendingFileTasks.Count > 0)
            {
                await DrainCompletedInitialVirtualFilesAsync(
                        state.PendingFileTasks,
                        state.PendingFileStates,
                        context,
                        waitForOne: true)
                    .ConfigureAwait(false);
            }

            await DeleteMissingInitialVirtualFilesRemoteDeletesAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.StreamingPlan,
                    state.StreamedRemoteFileKeys,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await FlushInitialVirtualFilesPopulationStateAsync(context, state).ConfigureAwait(false);
            await FinalizeInitialVirtualFilesDirectoryTreeAsync(context, state).ConfigureAwait(false);
        }

        private async Task FinalizeInitialVirtualFilesDirectoryTreeAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state)
        {
            if (state.DirectoryFinalizationRequests is not { Count: > 0 } requests
                || _remoteDirectoryTreePopulationObserver is null)
            {
                return;
            }

            int directoriesDiscovered = Math.Max(requests.Count, context.Metrics.DiscoveredDirectories);
            ReportRunProgress(
                context.Options,
                SyncRunProgressStage.FinalizingCloudFiles,
                0,
                directoriesDiscovered,
                null,
                context.StartedAtUtc);
            await _remoteDirectoryTreePopulationObserver
                .AfterDirectoryTreePopulationAsync(requests.Values.ToArray(), context.CancellationToken)
                .ConfigureAwait(false);
            ReportRunProgress(
                context.Options,
                SyncRunProgressStage.FinalizingCloudFiles,
                requests.Count,
                directoriesDiscovered,
                null,
                context.StartedAtUtc,
                isCompleted: true);
        }

        private async Task FlushInitialVirtualFilesPopulationStateAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state)
        {
            int flushedFileRows = await FlushInitialVirtualFilesStateBatchAsync(
                    state.PendingFileStates,
                    context.CancellationToken)
                .ConfigureAwait(false);
            context.Metrics.RecordFileStateWrite(flushedFileRows);
            int flushedDirectoryRows = await FlushInitialVirtualFilesStateBatchAsync(
                    state.PendingDirectoryStates,
                    context.CancellationToken)
                .ConfigureAwait(false);
            context.Metrics.RecordDirectoryStateWrite(flushedDirectoryRows);
        }

        private async Task DeleteMissingInitialVirtualFilesRemoteDeletesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            InitialVirtualFilesStreamingPlan streamingPlan,
            IReadOnlySet<string> streamedRemoteFileKeys,
            CancellationToken cancellationToken)
        {
            if (streamingPlan.CurrentPlaceholderBaselineByPath.Count == 0)
            {
                return;
            }

            List<InitialVirtualFilesPlaceholderBaseline> missingBaselines = [];
            foreach ((string pathKey, InitialVirtualFilesPlaceholderBaseline baseline) in streamingPlan.CurrentPlaceholderBaselineByPath)
            {
                if (!streamedRemoteFileKeys.Contains(pathKey))
                {
                    missingBaselines.Add(baseline);
                }
            }

            if (missingBaselines.Count == 0)
            {
                return;
            }

            SyncDeleteGuard deleteGuard = new(options, plannedLocalDeletes: missingBaselines.Count, plannedRemoteDeletes: 0);
            foreach (InitialVirtualFilesPlaceholderBaseline baseline in missingBaselines)
            {
                await DeleteLocalAsync(
                        syncPair,
                        options,
                        result,
                        deleteGuard,
                        baseline.RelativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task DrainCompletedInitialVirtualFilesAsync(
            List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>> pendingFileTasks,
            List<SyncStateEntry> pendingFileStates,
            InitialVirtualFilesPopulationContext context,
            bool waitForOne)
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
                        context)
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
                        context)
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
            InitialVirtualFilesPopulationContext context)
        {
            foreach (InitialVirtualFilesFileWorkResult workResult in workResults)
            {
                await CompleteInitialVirtualFilesFileWorkAsync(
                        workResult,
                        pendingFileStates,
                        context)
                    .ConfigureAwait(false);
            }
        }

        private async Task CompleteInitialVirtualFilesFileWorkAsync(
            InitialVirtualFilesFileWorkResult workResult,
            List<SyncStateEntry> pendingFileStates,
            InitialVirtualFilesPopulationContext context)
        {
            context.Metrics.RecordFileWorkResult(workResult);

            if (workResult.State is not null)
            {
                pendingFileStates.Add(workResult.State);
                if (pendingFileStates.Count >= context.Options.InitialVirtualFilesStateBatchSize)
                {
                    int flushedFileRows = await FlushInitialVirtualFilesStateBatchAsync(
                            pendingFileStates,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    context.Metrics.RecordFileStateWrite(flushedFileRows);
                }
            }

            int completedFiles = context.Metrics.RecordCompletedFile();
            if (ShouldReportInitialVirtualFilesFileProgress(workResult))
            {
                ReportInitialVirtualFilesStreamingProgress(context, workResult.RelativePath);
            }
            if (workResult.ReportActivity)
            {
                Report(
                    context.Result,
                    context.Options,
                    workResult.ActivityKind,
                    workResult.RelativePath,
                    workResult.Details,
                    workResult.RequiresUserAction,
                    publishActivityProgress: true);
            }

            await YieldAfterLargeBatchAsync(
                    context.Options,
                    GetInitialVirtualFilesItemCount(completedFiles, context.Metrics.CompletedDirectories),
                    Math.Max(
                        GetInitialVirtualFilesItemCount(completedFiles, context.Metrics.CompletedDirectories),
                        GetInitialVirtualFilesItemCount(
                            context.Metrics.DiscoveredFiles,
                            context.Metrics.DiscoveredDirectories)),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static void ReportInitialVirtualFilesStreamingProgress(
            InitialVirtualFilesPopulationContext context,
            string relativePath)
        {
            ReportStreamingVirtualFilesProgress(
                context.Options,
                context.Metrics.CompletedFiles,
                context.Metrics.DiscoveredFiles,
                context.Metrics.CompletedDirectories,
                context.Metrics.DiscoveredDirectories,
                context.Metrics.ExpectedItems,
                relativePath,
                context.StartedAtUtc,
                context.Metrics.LastPlaceholderProgressReportedAtUtc,
                value => context.Metrics.LastPlaceholderProgressReportedAtUtc = value);
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
            NodeFileManifestDto remoteFile,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                SyncPath.Normalize(relativePath),
                remoteFile,
                existingHydrationState);
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

        private InitialVirtualFilesFileWorkResult? TryCreateCurrentInitialVirtualFilesFileWorkResult(
            SyncPair syncPair,
            RemoteFileSnapshot remote,
            InitialVirtualFilesStreamingPlan streamingPlan)
        {
            if (!streamingPlan.SkipCurrentPlaceholders)
            {
                return null;
            }

            string key = SyncPath.ToKey(remote.RelativePath);
            if (streamingPlan.CurrentPlaceholderBaselineByPath.TryGetValue(
                    key,
                    out InitialVirtualFilesPlaceholderBaseline baseline)
                && HasRemoteFileBaseline(baseline)
                && RemoteMatchesBaseline(remote.File, baseline)
                && _localFilePresenceProbe?.FileExists(syncPair.LocalRootPath, remote.RelativePath) == true)
            {
                return new InitialVirtualFilesFileWorkResult(
                    remote.RelativePath,
                    State: null,
                    SyncActivityKind.Skipped,
                    Details: null,
                    RequiresUserAction: false,
                    ReportActivity: false);
            }

            return null;
        }

        private static bool CanAdoptUntrackedVirtualFilesPlaceholder(
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile)
        {
            return local.IsCloudFilesOnlineOnlyPlaceholder
                && local.SizeBytes == remoteFile.SizeBytes
                && DateTimesMatchWithinCloudFilesMetadataTolerance(local.LastWriteUtc, remoteFile.UpdatedAt);
        }

        private static bool ShouldReportInitialVirtualFilesFileProgress(InitialVirtualFilesFileWorkResult workResult)
        {
            return workResult.State is not null
                || workResult.ReportActivity
                || workResult.ActivityKind != SyncActivityKind.Skipped;
        }

        private static bool ReportStreamingVirtualFilesProgress(
            SyncRunOptions options,
            int filesCompleted,
            int filesDiscovered,
            int directoriesCompleted,
            int directoriesDiscovered,
            int expectedItems,
            string relativePath,
            DateTime startedAtUtc,
            DateTime? lastReportedAtUtc,
            Action<DateTime?> setLastReportedAtUtc)
        {
            int itemsCompleted = GetInitialVirtualFilesItemCount(filesCompleted, directoriesCompleted);
            int itemsDiscovered = GetInitialVirtualFilesItemCount(filesDiscovered, directoriesDiscovered);
            int itemsTotal = Math.Max(itemsCompleted, Math.Max(itemsDiscovered, expectedItems));
            DateTime occurredAtUtc = DateTime.UtcNow;
            if (!ShouldReportItemRunProgress(itemsCompleted, itemsTotal, lastReportedAtUtc, occurredAtUtc))
            {
                return false;
            }

            setLastReportedAtUtc(occurredAtUtc);
            ReportRunProgress(
                options,
                SyncRunProgressStage.CreatingPlaceholders,
                itemsCompleted,
                itemsTotal,
                relativePath,
                startedAtUtc);
            return true;
        }

        private static int GetInitialVirtualFilesItemCount(int fileCount, int directoryCount)
        {
            return checked(fileCount + directoryCount);
        }

        private async Task CoalesceRemoteDirectoryMovesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            if (directoryStateByPath.Count == 0)
            {
                return;
            }

            Dictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById = BuildUniqueRemoteDirectoriesById(
                remoteDirectoriesByPath.Values);
            Dictionary<Guid, RemoteFileSnapshot> remoteFilesById = BuildUniqueRemoteFilesById(remoteFilesByPath.Values);
            List<RemoteDirectoryMoveCandidate> accepted = FindRemoteDirectoryMoveCandidates(
                localDirectoriesByPath,
                localFilesByPath,
                directoryStateByPath,
                fileStateByPath,
                remoteDirectoriesById,
                remoteFilesById,
                cancellationToken);
            foreach (RemoteDirectoryMoveCandidate candidate in accepted)
            {
                await ApplyRemoteDirectoryMoveAsync(
                    syncPair,
                    options,
                    result,
                    candidate,
                    localDirectoriesByPath,
                    localFilesByPath,
                    directoryStateByPath,
                    fileStateByPath,
                    remoteDirectoriesById,
                    remoteFilesById,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private List<RemoteDirectoryMoveCandidate> FindRemoteDirectoryMoveCandidates(
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            CancellationToken cancellationToken)
        {
            List<RemoteDirectoryMoveCandidate> accepted = [];
            foreach (KeyValuePair<string, SyncStateEntry> source in directoryStateByPath
                         .OrderBy(entry => GetPathDepth(entry.Value.RelativePath))
                         .ThenBy(entry => entry.Value.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryCreateRemoteDirectoryMoveCandidate(
                        source,
                        accepted,
                        localDirectoriesByPath,
                        localFilesByPath,
                        directoryStateByPath,
                        fileStateByPath,
                        remoteDirectoriesById,
                        remoteFilesById,
                        out RemoteDirectoryMoveCandidate candidate))
                {
                    accepted.Add(candidate);
                }
            }

            return accepted;
        }

        private bool TryCreateRemoteDirectoryMoveCandidate(
            KeyValuePair<string, SyncStateEntry> source,
            IReadOnlyCollection<RemoteDirectoryMoveCandidate> accepted,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            out RemoteDirectoryMoveCandidate candidate)
        {
            candidate = default;
            if (!source.Value.RemoteNodeId.HasValue || !localDirectoriesByPath.ContainsKey(source.Key))
            {
                return false;
            }

            if (!remoteDirectoriesById.TryGetValue(
                    source.Value.RemoteNodeId.Value,
                    out RemoteDirectorySnapshot? target)
                || string.Equals(source.Value.RelativePath, target.RelativePath, StringComparison.Ordinal))
            {
                return false;
            }

            string sourceKey = SyncPath.ToKey(source.Value.RelativePath);
            if (accepted.Any(existing => IsSameOrDescendantPathKey(sourceKey, existing.SourceKey)))
            {
                return false;
            }

            candidate = new RemoteDirectoryMoveCandidate(
                source.Value.RelativePath,
                target.RelativePath,
                sourceKey,
                SyncPath.ToKey(target.RelativePath));
            if (!CanCoalesceRemoteDirectoryMove(
                    candidate,
                    localDirectoriesByPath,
                    localFilesByPath,
                    directoryStateByPath,
                    fileStateByPath,
                    remoteDirectoriesById,
                    remoteFilesById,
                    out string? rejectionReason))
            {
                _logger.LogInformation(
                    "Remote directory move from {SourcePath} to {TargetPath} was not coalesced: {Reason}",
                    candidate.SourcePath,
                    candidate.TargetPath,
                    rejectionReason);
                return false;
            }

            _logger.LogInformation(
                "Remote directory move from {SourcePath} to {TargetPath} passed stable-id validation.",
                candidate.SourcePath,
                candidate.TargetPath);
            return true;
        }

        private async Task ApplyRemoteDirectoryMoveAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureRemoteDirectoryMoveLocalHashesAsync(
                syncPair,
                options,
                candidate,
                localFilesByPath,
                fileStateByPath,
                cancellationToken).ConfigureAwait(false);
            await _localWriter.MoveDirectoryAsync(
                syncPair.LocalRootPath,
                candidate.SourcePath,
                candidate.TargetPath,
                cancellationToken).ConfigureAwait(false);
            MoveLocalDirectoryLookups(syncPair.LocalRootPath, candidate, localDirectoriesByPath);
            MoveLocalFileLookups(syncPair.LocalRootPath, candidate, localFilesByPath);

            List<KeyValuePair<string, SyncStateEntry>> movedDirectoryStates = directoryStateByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .OrderBy(entry => GetPathDepth(entry.Value.RelativePath))
                .ToList();
            await NotifyRemoteDirectoryMovePopulationAsync(
                syncPair,
                candidate,
                movedDirectoryStates,
                remoteDirectoriesById,
                cancellationToken).ConfigureAwait(false);
            await MoveRemoteDirectoryStatesAsync(
                syncPair,
                candidate,
                movedDirectoryStates,
                directoryStateByPath,
                remoteDirectoriesById,
                cancellationToken).ConfigureAwait(false);
            await MoveRemoteFileStatesAsync(
                syncPair,
                options,
                candidate,
                localFilesByPath,
                fileStateByPath,
                remoteFilesById,
                cancellationToken).ConfigureAwait(false);
            Report(
                result,
                options,
                SyncActivityKind.Moved,
                candidate.TargetPath,
                "Moved local folder to follow the remote folder path.");
        }

        private async Task NotifyRemoteDirectoryMovePopulationAsync(
            SyncPair syncPair,
            RemoteDirectoryMoveCandidate candidate,
            IEnumerable<KeyValuePair<string, SyncStateEntry>> movedDirectoryStates,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            CancellationToken cancellationToken)
        {
            if (_remoteDirectoryTreePopulationObserver is null)
            {
                return;
            }

            List<RemoteDirectoryMaterializationRequest> directoryRequests = movedDirectoryStates
                .Select(entry =>
                {
                    RemoteDirectorySnapshot remote = remoteDirectoriesById[entry.Value.RemoteNodeId!.Value];
                    string targetPath = ReplacePathPrefix(
                        entry.Value.RelativePath,
                        candidate.SourcePath,
                        candidate.TargetPath);
                    return CreateRemoteDirectoryMaterializationRequest(syncPair, targetPath, remote.Node);
                })
                .ToList();
            await _remoteDirectoryTreePopulationObserver
                .AfterDirectoryTreePopulationAsync(directoryRequests, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task MoveRemoteDirectoryStatesAsync(
            SyncPair syncPair,
            RemoteDirectoryMoveCandidate candidate,
            IEnumerable<KeyValuePair<string, SyncStateEntry>> movedDirectoryStates,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in movedDirectoryStates)
            {
                string targetPath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                RemoteDirectorySnapshot remote = remoteDirectoriesById[entry.Value.RemoteNodeId!.Value];
                SyncStateEntry movedState = BuildDirectoryBaseline(syncPair, targetPath, remote.Node);
                await MoveStateEntryAsync(
                    syncPair.SyncPairId,
                    entry.Value.RelativePath,
                    movedState,
                    directoryStateByPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task MoveRemoteFileStatesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, SyncStateEntry>> movedFileStates = fileStateByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .OrderBy(entry => entry.Value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (KeyValuePair<string, SyncStateEntry> entry in movedFileStates)
            {
                string targetPath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                string targetKey = SyncPath.ToKey(targetPath);
                RemoteFileSnapshot remote = remoteFilesById[entry.Value.RemoteFileId!.Value];
                LocalFileSnapshot local = localFilesByPath[targetKey];
                SyncStateEntry movedState = await BuildMovedRemoteFileStateAsync(
                    syncPair,
                    options,
                    targetPath,
                    local,
                    remote.File,
                    entry.Value,
                    cancellationToken).ConfigureAwait(false);
                await MoveStateEntryAsync(
                    syncPair.SyncPairId,
                    entry.Value.RelativePath,
                    movedState,
                    fileStateByPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task EnsureRemoteDirectoryMoveLocalHashesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in fileStateByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!localFilesByPath.TryGetValue(entry.Key, out LocalFileSnapshot? local)
                    || IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, entry.Value)
                    || string.IsNullOrWhiteSpace(entry.Value.LocalContentHash))
                {
                    continue;
                }

                await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
            }
        }

        private static Dictionary<Guid, RemoteDirectorySnapshot> BuildUniqueRemoteDirectoriesById(
            IEnumerable<RemoteDirectorySnapshot> directories)
        {
            var unique = new Dictionary<Guid, RemoteDirectorySnapshot>();
            HashSet<Guid> duplicates = [];
            foreach (RemoteDirectorySnapshot directory in directories)
            {
                if (!unique.TryAdd(directory.Node.Id, directory))
                {
                    duplicates.Add(directory.Node.Id);
                }
            }

            foreach (Guid duplicate in duplicates)
            {
                unique.Remove(duplicate);
            }

            return unique;
        }

        private static Dictionary<Guid, RemoteFileSnapshot> BuildUniqueRemoteFilesById(
            IEnumerable<RemoteFileSnapshot> files)
        {
            var unique = new Dictionary<Guid, RemoteFileSnapshot>();
            HashSet<Guid> duplicates = [];
            foreach (RemoteFileSnapshot file in files)
            {
                if (!unique.TryAdd(file.File.Id, file))
                {
                    duplicates.Add(file.File.Id);
                }
            }

            foreach (Guid duplicate in duplicates)
            {
                unique.Remove(duplicate);
            }

            return unique;
        }

        private static bool CanCoalesceRemoteDirectoryMove(
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            out string? rejectionReason)
        {
            if (!PathComparer.Equals(candidate.SourceKey, candidate.TargetKey)
                && IsSameOrDescendantPathKey(candidate.TargetKey, candidate.SourceKey))
            {
                rejectionReason = "the target path is inside the source subtree";
                return false;
            }

            HashSet<string> sourceDirectoryKeys = localDirectoriesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, candidate.SourceKey))
                .ToHashSet(PathComparer);
            HashSet<string> sourceFileKeys = localFilesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, candidate.SourceKey))
                .ToHashSet(PathComparer);
            rejectionReason = FindRemoteDirectoryMoveLocalCollision(
                candidate,
                sourceDirectoryKeys,
                sourceFileKeys,
                localDirectoriesByPath,
                localFilesByPath);
            if (rejectionReason is not null)
            {
                return false;
            }

            rejectionReason = ValidateTrackedRemoteDirectoryMoveDirectories(
                candidate,
                directoryStateByPath,
                localDirectoriesByPath,
                remoteDirectoriesById);
            if (rejectionReason is not null)
            {
                return false;
            }

            rejectionReason = ValidateTrackedRemoteDirectoryMoveFiles(
                candidate,
                fileStateByPath,
                localFilesByPath,
                remoteFilesById);
            return rejectionReason is null;
        }

        private static string? FindRemoteDirectoryMoveLocalCollision(
            RemoteDirectoryMoveCandidate candidate,
            IReadOnlySet<string> sourceDirectoryKeys,
            IReadOnlySet<string> sourceFileKeys,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath)
        {
            foreach (string sourceKey in sourceDirectoryKeys)
            {
                LocalDirectorySnapshot local = localDirectoriesByPath[sourceKey];
                string targetPath = ReplacePathPrefix(local.RelativePath, candidate.SourcePath, candidate.TargetPath);
                string targetKey = SyncPath.ToKey(targetPath);
                if ((localDirectoriesByPath.ContainsKey(targetKey) && !sourceDirectoryKeys.Contains(targetKey))
                    || (localFilesByPath.ContainsKey(targetKey) && !sourceFileKeys.Contains(targetKey)))
                {
                    return $"the target path '{targetPath}' collides with an existing local item";
                }
            }

            foreach (string sourceKey in sourceFileKeys)
            {
                LocalFileSnapshot local = localFilesByPath[sourceKey];
                string targetPath = ReplacePathPrefix(local.RelativePath, candidate.SourcePath, candidate.TargetPath);
                string targetKey = SyncPath.ToKey(targetPath);
                if ((localFilesByPath.ContainsKey(targetKey) && !sourceFileKeys.Contains(targetKey))
                    || (localDirectoriesByPath.ContainsKey(targetKey) && !sourceDirectoryKeys.Contains(targetKey)))
                {
                    return $"the target path '{targetPath}' collides with an existing local item";
                }
            }

            return null;
        }

        private static string? ValidateTrackedRemoteDirectoryMoveDirectories(
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in directoryStateByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey)))
            {
                if (!localDirectoriesByPath.ContainsKey(entry.Key))
                {
                    return $"tracked directory '{entry.Value.RelativePath}' is absent from the local snapshot";
                }

                if (!entry.Value.RemoteNodeId.HasValue)
                {
                    return $"tracked directory '{entry.Value.RelativePath}' has no remote node id";
                }

                if (!remoteDirectoriesById.TryGetValue(
                        entry.Value.RemoteNodeId.Value,
                        out RemoteDirectorySnapshot? remote))
                {
                    return $"tracked directory '{entry.Value.RelativePath}' is absent from the remote snapshot by id";
                }

                string expectedRemotePath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                if (!string.Equals(remote.RelativePath, expectedRemotePath, StringComparison.Ordinal))
                {
                    return $"tracked directory '{entry.Value.RelativePath}' maps to remote path '{remote.RelativePath}' instead of '{expectedRemotePath}'";
                }
            }

            return null;
        }

        private static string? ValidateTrackedRemoteDirectoryMoveFiles(
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in fileStateByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey)))
            {
                if (!localFilesByPath.ContainsKey(entry.Key))
                {
                    return $"tracked file '{entry.Value.RelativePath}' is absent from the local snapshot";
                }

                if (!entry.Value.RemoteFileId.HasValue)
                {
                    return $"tracked file '{entry.Value.RelativePath}' has no remote file id";
                }

                if (!remoteFilesById.TryGetValue(entry.Value.RemoteFileId.Value, out RemoteFileSnapshot? remote))
                {
                    return $"tracked file '{entry.Value.RelativePath}' is absent from the remote snapshot by id";
                }

                string expectedRemotePath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                if (!string.Equals(remote.RelativePath, expectedRemotePath, StringComparison.Ordinal))
                {
                    return $"tracked file '{entry.Value.RelativePath}' maps to remote path '{remote.RelativePath}' instead of '{expectedRemotePath}'";
                }
            }

            return null;
        }

        private static void MoveLocalDirectoryLookups(
            string localRootPath,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath)
        {
            List<KeyValuePair<string, LocalDirectorySnapshot>> moved = localDirectoriesByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .ToList();
            foreach (KeyValuePair<string, LocalDirectorySnapshot> entry in moved)
            {
                localDirectoriesByPath.Remove(entry.Key);
            }

            foreach (KeyValuePair<string, LocalDirectorySnapshot> entry in moved)
            {
                string targetPath = ReplacePathPrefix(entry.Value.RelativePath, candidate.SourcePath, candidate.TargetPath);
                entry.Value.RelativePath = targetPath;
                entry.Value.FullPath = ResolveLocalPath(localRootPath, targetPath);
                localDirectoriesByPath.Add(SyncPath.ToKey(targetPath), entry.Value);
            }
        }

        private static void MoveLocalFileLookups(
            string localRootPath,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalFileSnapshot> localFilesByPath)
        {
            List<KeyValuePair<string, LocalFileSnapshot>> moved = localFilesByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .ToList();
            foreach (KeyValuePair<string, LocalFileSnapshot> entry in moved)
            {
                localFilesByPath.Remove(entry.Key);
            }

            foreach (KeyValuePair<string, LocalFileSnapshot> entry in moved)
            {
                string targetPath = ReplacePathPrefix(entry.Value.RelativePath, candidate.SourcePath, candidate.TargetPath);
                entry.Value.RelativePath = targetPath;
                entry.Value.FullPath = ResolveLocalPath(localRootPath, targetPath);
                localFilesByPath.Add(SyncPath.ToKey(targetPath), entry.Value);
            }
        }

        private async Task<SyncStateEntry> BuildMovedRemoteFileStateAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetPath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            SyncStateEntry previousState,
            CancellationToken cancellationToken)
        {
            bool localMatchesBaseline = IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, previousState);
            if (!localMatchesBaseline && !string.IsNullOrWhiteSpace(previousState.LocalContentHash))
            {
                await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
                localMatchesBaseline = ContentMatches(local.ContentHash, previousState.LocalContentHash)
                    && (!previousState.LocalSizeBytes.HasValue || local.SizeBytes == previousState.LocalSizeBytes.Value);
            }

            if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && local.IsCloudFilesPlaceholder
                && localMatchesBaseline
                && _remoteFilePlaceholderWriter is not null)
            {
                RemoteFilePlaceholderResult placeholder = await _remoteFilePlaceholderWriter
                    .CreatePlaceholderAsync(
                        CreateRemoteFilePlaceholderRequest(
                            syncPair,
                            targetPath,
                            remoteFile,
                            previousState.PlaceholderHydrationState),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (placeholder.LocalSizeBytes.HasValue)
                {
                    local.SizeBytes = placeholder.LocalSizeBytes.Value;
                }

                if (placeholder.LocalLastWriteUtc.HasValue)
                {
                    local.LastWriteUtc = placeholder.LocalLastWriteUtc.Value.ToUniversalTime();
                }

                return BuildPlaceholderBaseline(
                    syncPair,
                    targetPath,
                    remoteFile,
                    placeholder,
                    previousState.PlaceholderHydrationState);
            }

            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(targetPath),
                Kind = SyncEntryKind.File,
                LocalContentHash = previousState.LocalContentHash,
                LocalLastWriteUtc = previousState.LocalLastWriteUtc,
                LocalSizeBytes = previousState.LocalSizeBytes,
                RemoteSizeBytes = previousState.RemoteSizeBytes,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = previousState.RemoteFileId,
                RemoteFileManifestId = previousState.RemoteFileManifestId,
                RemoteOriginalNodeFileId = previousState.RemoteOriginalNodeFileId,
                RemoteContentHash = previousState.RemoteContentHash,
                RemoteETag = previousState.RemoteETag,
                PlaceholderIdentity = previousState.PlaceholderIdentity,
                PlaceholderHydrationState = previousState.PlaceholderHydrationState,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private async Task MoveStateEntryAsync(
            string syncPairId,
            string sourcePath,
            SyncStateEntry movedState,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            string sourceKey = SyncPath.ToKey(sourcePath);
            string targetKey = SyncPath.ToKey(movedState.RelativePath);
            await _stateStore.UpsertAsync(movedState, cancellationToken).ConfigureAwait(false);
            if (!PathComparer.Equals(sourceKey, targetKey))
            {
                await _stateStore.DeleteAsync(syncPairId, sourcePath, cancellationToken).ConfigureAwait(false);
                stateByPath.Remove(sourceKey);
            }

            stateByPath[targetKey] = movedState;
        }

        private static string ReplacePathPrefix(string path, string sourcePrefix, string targetPrefix)
        {
            string normalizedPath = SyncPath.Normalize(path);
            string normalizedSource = SyncPath.Normalize(sourcePrefix);
            string normalizedTarget = SyncPath.Normalize(targetPrefix);
            if (PathComparer.Equals(normalizedPath, normalizedSource))
            {
                return normalizedTarget;
            }

            return normalizedTarget + normalizedPath[normalizedSource.Length..];
        }

        private static string ResolveLocalPath(string localRootPath, string relativePath)
        {
            return Path.Combine(
                Path.GetFullPath(localRootPath),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private async Task ReconcileDirectoriesWithoutBaselineAsync(DirectoryReconciliationContext context)
        {
            int foldersCompleted = 0;
            DateTime? lastDirectoryRunProgressReportedAtUtc = null;
            foreach (string key in context.PathKeys)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.LocalByPath.TryGetValue(key, out LocalDirectorySnapshot? local);
                context.RemoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                context.StateByPath.TryGetValue(key, out SyncStateEntry? state);
                string relativePath = ResolveDirectoryRelativePath(key, local, remote, state);
                ReportDirectoryProgress(
                    context,
                    foldersCompleted,
                    relativePath,
                    ref lastDirectoryRunProgressReportedAtUtc);
                if (state is null)
                {
                    await ReconcileDirectoryWithoutBaselineAsync(context, relativePath, local, remote)
                        .ConfigureAwait(false);
                }

                foldersCompleted++;
                ReportDirectoryProgress(
                    context,
                    foldersCompleted,
                    relativePath,
                    ref lastDirectoryRunProgressReportedAtUtc);
            }
        }

        private async Task ReconcileDirectoryWithoutBaselineAsync(
            DirectoryReconciliationContext context,
            string relativePath,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote)
        {
            if (local is null)
            {
                if (remote is null)
                {
                    return;
                }

                await CreateRemoteBackedLocalDirectoryAsync(
                        context.SyncPair,
                        relativePath,
                        remote.Node,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                await _stateStore.UpsertAsync(
                        BuildDirectoryBaseline(context.SyncPair, relativePath, remote.Node),
                        context.CancellationToken)
                    .ConfigureAwait(false);
                Report(context.Result, context.Options, SyncActivityKind.Downloaded, relativePath, "Created local folder.");
                return;
            }

            if (remote is null)
            {
                if (_remoteDirectories is not null)
                {
                    await CreateRemoteDirectoryWithoutBaselineAsync(context, relativePath).ConfigureAwait(false);
                }

                return;
            }

            await _stateStore.UpsertAsync(
                    BuildDirectoryBaseline(context.SyncPair, relativePath, remote.Node),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private async Task CreateRemoteDirectoryWithoutBaselineAsync(
            DirectoryReconciliationContext context,
            string relativePath)
        {
            string parentPath = GetParentPath(relativePath);
            string parentKey = string.IsNullOrEmpty(parentPath) ? string.Empty : SyncPath.ToKey(parentPath);
            if (!TryGetRemoteDirectoryNodeId(
                    context.RemoteByPath,
                    parentKey,
                    context.RemoteRootNode.Id,
                    out Guid parentNodeId))
            {
                return;
            }

            RemoteDirectoryCreationResult creation = await CreateOrReuseRemoteDirectoryAsync(
                    _remoteDirectories!,
                    parentNodeId,
                    GetFileName(relativePath),
                    context.CancellationToken)
                .ConfigureAwait(false);
            var createdSnapshot = new RemoteDirectorySnapshot
            {
                RelativePath = relativePath,
                Node = creation.Node,
            };
            context.RemoteByPath[SyncPath.ToKey(relativePath)] = createdSnapshot;
            await _stateStore.UpsertAsync(
                    BuildDirectoryBaseline(context.SyncPair, relativePath, creation.Node),
                    context.CancellationToken)
                .ConfigureAwait(false);
            string details = creation.ReusedExisting
                ? "Reused existing remote folder after create conflict."
                : "Created remote folder.";
            Report(context.Result, context.Options, SyncActivityKind.Uploaded, relativePath, details);
        }

        private static string ResolveDirectoryRelativePath(
            string key,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote,
            SyncStateEntry? state)
        {
            if (local is not null)
            {
                return local.RelativePath;
            }

            if (remote is not null)
            {
                return remote.RelativePath;
            }

            return state is not null ? state.RelativePath : key;
        }

        private static void ReportDirectoryProgress(
            DirectoryReconciliationContext context,
            int foldersCompleted,
            string relativePath,
            ref DateTime? lastReportedAtUtc)
        {
            ReportItemRunProgress(
                context.Options,
                SyncRunProgressStage.ReconcilingDirectories,
                foldersCompleted,
                context.PathKeys.Count,
                relativePath,
                context.StartedAtUtc,
                ref lastReportedAtUtc);
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

        private async Task ReconcileDirectoryDeletesAsync(DirectoryDeleteContext context)
        {
            foreach (string key in EnumerateDirectoryDeleteKeys(context.PathKeys))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!TryGetDirectoryDeleteState(context, key, out SyncStateEntry? state))
                {
                    continue;
                }

                context.LocalByPath.TryGetValue(key, out LocalDirectorySnapshot? local);
                context.RemoteByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                string relativePath = ResolveDirectoryRelativePath(key, local, remote, state);
                await ReconcileDirectoryDeleteAsync(context, key, relativePath, local, remote).ConfigureAwait(false);
            }
        }

        private static bool TryGetDirectoryDeleteState(
            DirectoryDeleteContext context,
            string key,
            out SyncStateEntry? state)
        {
            if (context.PlannedScopedDeleteKeys?.Contains(key, PathComparer) == true)
            {
                state = null;
                return false;
            }

            if (context.ScopedDeleteKeys is not null && !context.ScopedDeleteKeys.Contains(key))
            {
                state = null;
                return false;
            }

            return context.StateByPath.TryGetValue(key, out state);
        }

        private async Task ReconcileDirectoryDeleteAsync(
            DirectoryDeleteContext context,
            string key,
            string relativePath,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote)
        {
            if (local is null && remote is null)
            {
                await _stateStore.DeleteAsync(
                        context.SyncPair.SyncPairId,
                        relativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (local is null && remote is not null)
            {
                await DeleteRemoteDirectoryAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.DeleteGuard,
                        relativePath,
                        remote,
                        context.RemoteContentIndex,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (remote is null && local is not null)
            {
                await ReconcileRemoteDeletedDirectoryAsync(context, key, relativePath, local).ConfigureAwait(false);
            }
        }

        private async Task ReconcileRemoteDeletedDirectoryAsync(
            DirectoryDeleteContext context,
            string key,
            string relativePath,
            LocalDirectorySnapshot local)
        {
            bool isNotEmpty = context.LocalContentIndex.HasChildren(relativePath)
                || DirectoryHasFileSystemEntries(local.FullPath);
            if (isNotEmpty)
            {
                if (CanDeferConfirmedRemoteDeletedDirectory(context, key))
                {
                    return;
                }

                Report(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    "Local folder delete skipped because the folder is not empty.");
                return;
            }

            await DeleteLocalDirectoryAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.DeleteGuard,
                    relativePath,
                    local,
                    context.LocalContentIndex,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static bool CanDeferConfirmedRemoteDeletedDirectory(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            if (!context.LocalByPath.TryGetValue(directoryKey, out LocalDirectorySnapshot? rootDirectory))
            {
                return false;
            }

            return !HasBlockingTrackedDirectoryDescendant(context, directoryKey)
                && !HasBlockingTrackedFileDescendant(context, directoryKey)
                && !HasUnknownFileSystemDirectory(rootDirectory, context.LocalByPath)
                && !HasUnknownFileSystemFile(rootDirectory, context.LocalFilesByPath);
        }

        private static bool HasBlockingTrackedDirectoryDescendant(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            foreach (string childDirectoryKey in context.LocalByPath.Keys
                         .Where(key => !PathComparer.Equals(key, directoryKey)
                             && IsSameOrDescendantPathKey(key, directoryKey)))
            {
                if (!context.StateByPath.ContainsKey(childDirectoryKey)
                    || context.RemoteByPath.ContainsKey(childDirectoryKey))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasBlockingTrackedFileDescendant(
            DirectoryDeleteContext context,
            string directoryKey)
        {
            foreach (string childFileKey in context.LocalFilesByPath.Keys
                         .Where(key => IsSameOrDescendantPathKey(key, directoryKey)))
            {
                if (!context.FileStateByPath.ContainsKey(childFileKey)
                    || context.RemoteFilesByPath.ContainsKey(childFileKey))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasUnknownFileSystemDirectory(
            LocalDirectorySnapshot rootDirectory,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath)
        {
            foreach (string childDirectoryPath in Directory.EnumerateDirectories(
                         rootDirectory.FullPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relativePath = CombineRelativePath(
                    rootDirectory.RelativePath,
                    Path.GetRelativePath(rootDirectory.FullPath, childDirectoryPath));
                if (!SyncPathIgnoreRules.ShouldIgnore(relativePath)
                    && !localDirectoriesByPath.ContainsKey(SyncPath.ToKey(relativePath)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasUnknownFileSystemFile(
            LocalDirectorySnapshot rootDirectory,
            IReadOnlyDictionary<string, LocalFileSnapshot> localFilesByPath)
        {
            foreach (string childFilePath in Directory.EnumerateFiles(
                         rootDirectory.FullPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relativePath = CombineRelativePath(
                    rootDirectory.RelativePath,
                    Path.GetRelativePath(rootDirectory.FullPath, childFilePath));
                if (!SyncPathIgnoreRules.ShouldIgnore(relativePath)
                    && !localFilesByPath.ContainsKey(SyncPath.ToKey(relativePath)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CombineRelativePath(string parentPath, string childPath)
        {
            string normalizedChild = childPath.Replace(Path.DirectorySeparatorChar, '/');
            return SyncPath.Normalize(parentPath + "/" + normalizedChild);
        }

        private async Task ReconcileEmptyLocalDirectoriesAfterFileDeletesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            IReadOnlyList<string> pathKeys,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            foreach (string key in EnumerateDirectoryDeleteKeys(pathKeys))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!stateByPath.TryGetValue(key, out SyncStateEntry? state)
                    || !localByPath.TryGetValue(key, out LocalDirectorySnapshot? local)
                    || remoteByPath.ContainsKey(key)
                    || !Directory.Exists(local.FullPath))
                {
                    continue;
                }

                string relativePath = local.RelativePath;
                if (DirectoryHasFileSystemEntries(local.FullPath))
                {
                    continue;
                }

                await DeleteLocalDirectoryAsync(
                    syncPair,
                    options,
                    result,
                    deleteGuard,
                    relativePath,
                    local,
                    DirectoryContentIndex.Empty,
                    cancellationToken).ConfigureAwait(false);
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
            if (local is null)
            {
                if (remote is not null)
                {
                    await MaterializeRemoteOnlyFileAsync(
                            syncPair,
                            options,
                            result,
                            relativePath,
                            remote.File,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            if (remote is null)
            {
                await ReconcileLocalOnlyWithoutBaselineAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        blockLocalOnlyUploads,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await ReconcileLocalAndRemoteWithoutBaselineAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local,
                    remote.File,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task ReconcileLocalOnlyWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            bool blockLocalOnlyUploads,
            CancellationToken cancellationToken)
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
        }

        private async Task ReconcileLocalAndRemoteWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && local.IsCloudFilesOnlineOnlyPlaceholder)
            {
                await MaterializeRemoteOnlyFileAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
            if (!ContentMatches(local.ContentHash, remoteFile.ContentHash))
            {
                await PreserveConflictAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await _stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remoteFile),
                    cancellationToken)
                .ConfigureAwait(false);
            if (ShouldFinalizeConvergedLocalFile(syncPair, local))
            {
                Report(
                    result,
                    options,
                    SyncActivityKind.Converged,
                    relativePath,
                    "Local and remote content already matched.");
            }
        }

        private async Task ReconcileWithBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            IReadOnlySet<string>? scopedFileDeleteKeys,
            IReadOnlySet<string> scopedLocalDeletedFileKeys,
            SyncStateEntry state,
            string relativePath,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            CancellationToken cancellationToken)
        {
            var context = new SyncFileReconciliationContext(
                syncPair,
                options,
                result,
                deleteGuard,
                scopedFileDeleteKeys,
                scopedLocalDeletedFileKeys,
                state,
                relativePath,
                local,
                remote,
                cancellationToken);
            if (await TryMaterializeIncompletePlaceholderAsync(context).ConfigureAwait(false))
            {
                return;
            }

            await EnsureReconciliationLocalContentHashAsync(context).ConfigureAwait(false);
            SyncFileChangeState changeState = CreateFileChangeState(state, local, remote);
            if (IsDeleteOutsideScope(context, changeState))
            {
                return;
            }

            if (await TryReconcileMissingTrackedFileAsync(context).ConfigureAwait(false)
                || await TryReconcileMissingOnlineOnlyPlaceholderAsync(context, changeState).ConfigureAwait(false)
                || await TryReconcilePresentOnlineOnlyPlaceholderAsync(context, changeState).ConfigureAwait(false)
                || await TryReconcileConvergedFileAsync(context).ConfigureAwait(false))
            {
                return;
            }

            SyncFileChangeKind changeKind = ResolveTrackedFileChange(changeState);
            await ReconcileTrackedFileChangeAsync(context, changeKind).ConfigureAwait(false);
        }

        private async Task<bool> TryMaterializeIncompletePlaceholderAsync(SyncFileReconciliationContext context)
        {
            if (context.SyncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || context.Local is not { IsCloudFilesOnlineOnlyPlaceholder: true }
                || context.Remote is null
                || !IsIncompleteOnlineOnlyPlaceholderBaseline(context.State))
            {
                return false;
            }

            await MaterializeRemoteOnlyFileAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.RelativePath,
                    context.Remote.File,
                    context.CancellationToken,
                    context.State.PlaceholderHydrationState)
                .ConfigureAwait(false);
            return true;
        }

        private async Task EnsureReconciliationLocalContentHashAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is null)
            {
                return;
            }

            await EnsureLocalContentHashForBaselineComparisonAsync(
                    context.Local,
                    context.State,
                    context.Options,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static bool IsDeleteOutsideScope(
            SyncFileReconciliationContext context,
            SyncFileChangeState changeState)
        {
            return (changeState.LocalDeleted || changeState.RemoteDeleted)
                && !IsScopedDeleteAllowed(context.ScopedFileDeleteKeys, context.PathKey);
        }

        private async Task<bool> TryReconcileMissingTrackedFileAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is not null || context.Remote is not null)
            {
                return false;
            }

            await _stateStore.DeleteAsync(
                    context.SyncPair.SyncPairId,
                    context.RelativePath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        private async Task<bool> TryReconcileMissingOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            SyncFileChangeState changeState)
        {
            if (!IsOnlineOnlyPlaceholderBaseline(context.SyncPair, context.State))
            {
                return false;
            }

            if (context.Local is null && context.Remote is not null)
            {
                await ReconcileMissingLocalOnlineOnlyPlaceholderAsync(context, changeState.RemoteChanged)
                    .ConfigureAwait(false);
                return true;
            }

            if (!changeState.RemoteDeleted)
            {
                return false;
            }

            await ReconcileRemoteDeletedOnlineOnlyPlaceholderAsync(context).ConfigureAwait(false);
            return true;
        }

        private async Task ReconcileMissingLocalOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            bool remoteChanged)
        {
            if (context.IsExactLocalDelete && remoteChanged)
            {
                await PreserveConflictAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.RelativePath,
                        null,
                        context.Remote!.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.IsExactLocalDelete)
            {
                await DeleteRemoteAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.DeleteGuard,
                        context.RelativePath,
                        context.Remote!.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (remoteChanged || context.Options.RestoreMissingRemoteOnlyPlaceholders)
            {
                await MaterializeRemoteOnlyFileAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.RelativePath,
                        context.Remote!.File,
                        context.CancellationToken,
                        context.State.PlaceholderHydrationState)
                    .ConfigureAwait(false);
                return;
            }

            if (!context.Options.Scope.IsFull)
            {
                return;
            }

            Report(
                context.Result,
                context.Options,
                SyncActivityKind.Skipped,
                context.RelativePath,
                VirtualFileUserFacingCopy.RemoteOnlyLocalChangeRequiresActionMessage,
                requiresUserAction: true);
        }

        private async Task ReconcileRemoteDeletedOnlineOnlyPlaceholderAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is null)
            {
                await _stateStore.DeleteAsync(
                        context.SyncPair.SyncPairId,
                        context.RelativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (IsLocalOnlineOnlyPlaceholderBaseline(context.SyncPair, context.Local, context.State))
            {
                await DeleteLocalAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.DeleteGuard,
                        context.RelativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await PreserveConflictAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.RelativePath,
                    context.Local,
                    null,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<bool> TryReconcilePresentOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            SyncFileChangeState changeState)
        {
            if (context.Local is null || context.Remote is null)
            {
                return false;
            }

            if (IsLocalOnlineOnlyPlaceholderBaseline(context.SyncPair, context.Local, context.State))
            {
                if (changeState.RemoteChanged)
                {
                    await MaterializeRemoteOnlyFileAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Remote.File,
                            context.CancellationToken,
                            context.State.PlaceholderHydrationState)
                        .ConfigureAwait(false);
                }

                return true;
            }

            if (!IsOnlineOnlyPlaceholderBaseline(context.SyncPair, context.State))
            {
                return false;
            }

            await ReconcileHydratedOnlineOnlyPlaceholderAsync(context, changeState.RemoteChanged).ConfigureAwait(false);
            return true;
        }

        private async Task ReconcileHydratedOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            bool remoteChanged)
        {
            if (ContentMatches(context.Local!.ContentHash, context.Remote!.File.ContentHash))
            {
                await _stateStore.UpsertAsync(
                        BuildHydratedPlaceholderBaseline(
                            context.SyncPair,
                            context.RelativePath,
                            context.Local,
                            context.Remote.File,
                            context.State),
                        context.CancellationToken)
                    .ConfigureAwait(false);
                Report(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Converged,
                    context.RelativePath,
                    "Hydrated placeholder content matches the remote file.");
                return;
            }

            if (!remoteChanged)
            {
                await UploadAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.RelativePath,
                        context.Local,
                        context.Remote.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await PreserveConflictAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.RelativePath,
                    context.Local,
                    context.Remote.File,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<bool> TryReconcileConvergedFileAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is null
                || context.Remote is null
                || !ContentMatches(context.Local.ContentHash, context.Remote.File.ContentHash))
            {
                return false;
            }

            if (!BaselineMatchesCurrentFile(
                    context.SyncPair,
                    context.RelativePath,
                    context.State,
                    context.Local,
                    context.Remote.File))
            {
                await _stateStore.UpsertAsync(
                        BuildBaseline(
                            context.SyncPair,
                            context.RelativePath,
                            context.Local.ContentHash,
                            context.Local.LastWriteUtc,
                            context.Local.SizeBytes,
                            context.Remote.File),
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (ShouldFinalizeConvergedLocalFile(context.SyncPair, context.Local))
            {
                Report(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Converged,
                    context.RelativePath,
                    "Local and remote content are synchronized.");
            }

            return true;
        }

        private async Task ReconcileTrackedFileChangeAsync(
            SyncFileReconciliationContext context,
            SyncFileChangeKind changeKind)
        {
            switch (changeKind)
            {
                case SyncFileChangeKind.None:
                    return;
                case SyncFileChangeKind.DeleteState:
                    await _stateStore.DeleteAsync(
                            context.SyncPair.SyncPairId,
                            context.RelativePath,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.DeleteLocal:
                    await DeleteLocalAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.DeleteGuard,
                            context.RelativePath,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.DeleteRemote:
                    await DeleteRemoteAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.DeleteGuard,
                            context.RelativePath,
                            context.Remote!.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.Upload:
                    await UploadAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Local!,
                            context.Remote?.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.Download:
                    await DownloadAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Remote!.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.Conflict:
                    await PreserveConflictAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Local,
                            context.Remote?.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null);
            }
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

        private async Task CoalesceLocalOnlineOnlyPlaceholderMovesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                return;
            }

            var context = new OnlineOnlyPlaceholderMoveContext(
                syncPair,
                options,
                result,
                localByPath,
                remoteByPath,
                stateByPath,
                cancellationToken);
            IReadOnlySet<string> scopedKeys = BuildExactScopedPathKeys(options.Scope.LocalChangedPaths);
            IReadOnlySet<string> explicitlyDeletedKeys = BuildExactScopedPathKeys(options.Scope.LocalDeletedPaths);
            IReadOnlyList<OnlineOnlyPlaceholderMoveSource> sources = FindOnlineOnlyPlaceholderMoveSources(
                context,
                explicitlyDeletedKeys);
            if (sources.Count == 0)
            {
                return;
            }

            IReadOnlyList<OnlineOnlyPlaceholderMoveTarget> targets = FindOnlineOnlyPlaceholderMoveTargets(context);
            IReadOnlyList<OnlineOnlyPlaceholderMoveMatch> matches = FindUnambiguousOnlineOnlyPlaceholderMoveMatches(
                options,
                scopedKeys,
                sources,
                targets);
            foreach (OnlineOnlyPlaceholderMoveMatch match in matches)
            {
                await ApplyOnlineOnlyPlaceholderMoveAsync(context, match).ConfigureAwait(false);
            }
        }

        private static IReadOnlyList<OnlineOnlyPlaceholderMoveSource> FindOnlineOnlyPlaceholderMoveSources(
            OnlineOnlyPlaceholderMoveContext context,
            IReadOnlySet<string> explicitlyDeletedKeys)
        {
            List<OnlineOnlyPlaceholderMoveSource> sources = [];
            foreach (KeyValuePair<string, SyncStateEntry> state in context.StateByPath)
            {
                if (!IsOnlineOnlyPlaceholderState(state.Value)
                    || state.Value.PlaceholderIdentity is not { Length: > 0 }
                    || explicitlyDeletedKeys.Contains(state.Key)
                    || context.LocalByPath.ContainsKey(state.Key)
                    || !context.RemoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote)
                    || !RemoteMatchesBaseline(remote.File, state.Value))
                {
                    continue;
                }

                sources.Add(new OnlineOnlyPlaceholderMoveSource(state.Key, state.Value, remote));
            }

            return sources;
        }

        private static IReadOnlyList<OnlineOnlyPlaceholderMoveTarget> FindOnlineOnlyPlaceholderMoveTargets(
            OnlineOnlyPlaceholderMoveContext context)
        {
            List<OnlineOnlyPlaceholderMoveTarget> targets = [];
            foreach (KeyValuePair<string, LocalFileSnapshot> local in context.LocalByPath)
            {
                if (local.Value.IsCloudFilesOnlineOnlyPlaceholder
                    && !context.StateByPath.ContainsKey(local.Key)
                    && !context.RemoteByPath.ContainsKey(local.Key))
                {
                    targets.Add(new OnlineOnlyPlaceholderMoveTarget(local.Key, local.Value));
                }
            }

            return targets;
        }

        private static IReadOnlyList<OnlineOnlyPlaceholderMoveMatch> FindUnambiguousOnlineOnlyPlaceholderMoveMatches(
            SyncRunOptions options,
            IReadOnlySet<string> scopedKeys,
            IReadOnlyList<OnlineOnlyPlaceholderMoveSource> sources,
            IReadOnlyList<OnlineOnlyPlaceholderMoveTarget> targets)
        {
            List<OnlineOnlyPlaceholderMoveMatch> matches = [];
            foreach (OnlineOnlyPlaceholderMoveSource source in sources)
            {
                OnlineOnlyPlaceholderMoveTarget[] matchingTargets = targets
                    .Where(target => CanCoalesceOnlineOnlyPlaceholderMove(
                        source.State,
                        source.Remote.File,
                        target.Local,
                        CanUseScopedOnlineOnlyPlaceholderRename(
                            options,
                            scopedKeys,
                            source.SourceKey,
                            target.TargetKey,
                            source.State.RelativePath,
                            target.Local.RelativePath)))
                    .ToArray();
                if (matchingTargets.Length != 1)
                {
                    continue;
                }

                OnlineOnlyPlaceholderMoveTarget target = matchingTargets[0];
                int matchingSourceCount = sources.Count(
                    candidate => CanCoalesceOnlineOnlyPlaceholderMove(
                        candidate.State,
                        candidate.Remote.File,
                        target.Local,
                        CanUseScopedOnlineOnlyPlaceholderRename(
                            options,
                            scopedKeys,
                            candidate.SourceKey,
                            target.TargetKey,
                            candidate.State.RelativePath,
                            target.Local.RelativePath)));
                if (matchingSourceCount == 1)
                {
                    matches.Add(new OnlineOnlyPlaceholderMoveMatch(
                        source.SourceKey,
                        source.State,
                        source.Remote,
                        target.TargetKey,
                        target.Local));
                }
            }

            return matches;
        }

        private async Task ApplyOnlineOnlyPlaceholderMoveAsync(
            OnlineOnlyPlaceholderMoveContext context,
            OnlineOnlyPlaceholderMoveMatch match)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            NodeFileManifestDto? moved = await TryMoveOnlineOnlyPlaceholderRemoteFileAsync(context, match)
                .ConfigureAwait(false);
            if (moved is null)
            {
                return;
            }

            string sourcePath = match.SourceState.RelativePath;
            string targetPath = match.Local.RelativePath;
            SyncStateEntry? targetState = await TryCreateRemoteOnlyFilePlaceholderStateAsync(
                    context.SyncPair,
                    context.Options,
                    targetPath,
                    moved,
                    context.CancellationToken,
                    match.SourceState.PlaceholderHydrationState)
                .ConfigureAwait(false);
            if (targetState is null)
            {
                throw new InvalidOperationException("Cloud Files placeholder refresh returned no state for " + targetPath + ".");
            }

            context.RemoteByPath.Remove(match.SourceKey);
            context.RemoteByPath[match.TargetKey] = new RemoteFileSnapshot
            {
                RelativePath = targetPath,
                File = moved,
            };
            context.StateByPath.Remove(match.SourceKey);
            context.StateByPath[match.TargetKey] = targetState;
            await _stateStore.DeleteAsync(context.SyncPair.SyncPairId, sourcePath, context.CancellationToken)
                .ConfigureAwait(false);
            await _stateStore.UpsertAsync(targetState, context.CancellationToken).ConfigureAwait(false);
            Report(context.Result, context.Options, SyncActivityKind.Moved, targetPath, "Moved from " + sourcePath + ".");
        }

        private async Task<NodeFileManifestDto?> TryMoveOnlineOnlyPlaceholderRemoteFileAsync(
            OnlineOnlyPlaceholderMoveContext context,
            OnlineOnlyPlaceholderMoveMatch match)
        {
            try
            {
                return await _remoteFiles
                    .MoveFileAsync(
                        context.SyncPair.RemoteRootNodeId,
                        match.Local.RelativePath,
                        match.Remote.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsRemotePreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await FindLatestRemoteFileAsync(
                        context.SyncPair,
                        match.SourceState.RelativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (latestRemoteFile is null)
                {
                    context.RemoteByPath.Remove(match.SourceKey);
                }
                else
                {
                    context.RemoteByPath[match.SourceKey] = new RemoteFileSnapshot
                    {
                        RelativePath = match.SourceState.RelativePath,
                        File = latestRemoteFile,
                    };
                }

                return null;
            }
        }

        private async Task DeleteConfirmedScopedVirtualFilesDirectoryRenameSourceAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            ScopedVirtualFilesDirectoryRenamePlan plan,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IReadOnlyDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            if (plan.SourceFileKeys.Any(key => remoteFilesByPath.ContainsKey(key) || fileStateByPath.ContainsKey(key)))
            {
                return;
            }

            if (_remoteDirectories is null)
            {
                Report(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    plan.SourceRootPath,
                    "Remote folder cleanup is not available after the confirmed local folder move.",
                    requiresUserAction: true);
                return;
            }

            foreach (string key in plan.SourceDirectoryKeys)
            {
                if (!directoryStateByPath.TryGetValue(key, out SyncStateEntry? state)
                    || !remoteDirectoriesByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote)
                    || state.RemoteNodeId != remote.Node.Id)
                {
                    return;
                }
            }

            SyncDeleteGuard deleteGuard = new(
                options,
                plannedLocalDeletes: 0,
                plannedRemoteDeletes: plan.SourceDirectoryKeys.Count);
            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                Report(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    plan.SourceRootPath,
                    details,
                    requiresUserAction: true);
                return;
            }

            foreach (string key in plan.SourceDirectoryKeys
                         .OrderByDescending(GetPathDepth)
                         .ThenBy(static key => key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncStateEntry state = directoryStateByPath[key];
                RemoteDirectorySnapshot remote = remoteDirectoriesByPath[key];
                await _remoteDirectories
                    .DeleteDirectoryAsync(remote.Node.Id, options.DeleteRemotePermanently, cancellationToken)
                    .ConfigureAwait(false);
                await _stateStore
                    .DeleteAsync(syncPair.SyncPairId, state.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
                directoryStateByPath.Remove(key);
                remoteDirectoriesByPath.Remove(key);
                Report(
                    result,
                    options,
                    SyncActivityKind.DeletedRemote,
                    state.RelativePath,
                    "Deleted source folder after confirmed local subtree move.");
            }
        }

        private async Task DeleteConfirmedScopedVirtualFilesDirectorySubtreesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            ScopedVirtualFilesDirectoryDeletePlan plan,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            CancellationToken cancellationToken)
        {
            if (await HasRemainingScopedVirtualFilesAsync(
                    syncPair.SyncPairId,
                    plan.FilePaths,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            IRemoteDirectorySynchronizer? remoteDirectories = _remoteDirectories;
            if (remoteDirectories is null)
            {
                ReportScopedDirectoryDeleteSkipped(
                    result,
                    options,
                    plan.RootPaths,
                    "Remote folder cleanup is not available after the confirmed local subtree delete.");
                return;
            }

            if (!AreScopedRemoteDirectoriesCurrent(
                    plan.DirectoryKeys,
                    remoteDirectoriesByPath,
                    directoryStateByPath))
            {
                return;
            }

            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                ReportScopedDirectoryDeleteSkipped(result, options, plan.RootPaths, details);
                return;
            }

            await DeleteScopedRemoteDirectoriesAsync(
                syncPair,
                options,
                result,
                remoteDirectories,
                plan.DirectoryKeys,
                remoteDirectoriesByPath,
                directoryStateByPath,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> HasRemainingScopedVirtualFilesAsync(
            string syncPairId,
            IEnumerable<string> relativePaths,
            CancellationToken cancellationToken)
        {
            foreach (string relativePath in relativePaths)
            {
                SyncStateEntry? remaining = await _stateStore
                    .GetAsync(syncPairId, relativePath, cancellationToken)
                    .ConfigureAwait(false);
                if (remaining is not null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AreScopedRemoteDirectoriesCurrent(
            IEnumerable<string> directoryKeys,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (string key in directoryKeys)
            {
                if (!directoryStateByPath.TryGetValue(key, out SyncStateEntry? state)
                    || !remoteDirectoriesByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote)
                    || state.RemoteNodeId != remote.Node.Id)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ReportScopedDirectoryDeleteSkipped(
            SyncRunResult result,
            SyncRunOptions options,
            IEnumerable<string> rootPaths,
            string? details)
        {
            foreach (string rootPath in rootPaths)
            {
                Report(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    rootPath,
                    details,
                    requiresUserAction: true);
            }
        }

        private async Task DeleteScopedRemoteDirectoriesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IRemoteDirectorySynchronizer remoteDirectories,
            IEnumerable<string> directoryKeys,
            IDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            CancellationToken cancellationToken)
        {
            foreach (string key in directoryKeys
                         .OrderByDescending(GetPathDepth)
                         .ThenBy(static key => key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncStateEntry state = directoryStateByPath[key];
                RemoteDirectorySnapshot remote = remoteDirectoriesByPath[key];
                await remoteDirectories
                    .DeleteDirectoryAsync(remote.Node.Id, options.DeleteRemotePermanently, cancellationToken)
                    .ConfigureAwait(false);
                await _stateStore
                    .DeleteAsync(syncPair.SyncPairId, state.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
                directoryStateByPath.Remove(key);
                remoteDirectoriesByPath.Remove(key);
                Report(
                    result,
                    options,
                    SyncActivityKind.DeletedRemote,
                    state.RelativePath,
                    "Deleted folder after confirmed local subtree delete.");
            }
        }

        private static bool CanCoalesceOnlineOnlyPlaceholderMove(
            SyncStateEntry sourceState,
            NodeFileManifestDto remoteFile,
            LocalFileSnapshot target,
            bool allowChangedFileName)
        {
            return (allowChangedFileName
                    || string.Equals(
                        Path.GetFileName(sourceState.RelativePath),
                        Path.GetFileName(target.RelativePath),
                        StringComparison.OrdinalIgnoreCase))
                && CanAdoptUntrackedVirtualFilesPlaceholder(target, remoteFile);
        }

        private static bool CanUseScopedOnlineOnlyPlaceholderRename(
            SyncRunOptions options,
            IReadOnlySet<string> scopedKeys,
            string sourceKey,
            string targetKey,
            string sourcePath,
            string targetPath)
        {
            return !options.Scope.IsFull
                && scopedKeys.Contains(sourceKey)
                && scopedKeys.Contains(targetKey)
                && PathComparer.Equals(
                    SyncPath.ToKey(GetParentPath(sourcePath)),
                    SyncPath.ToKey(GetParentPath(targetPath)));
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

            await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
            NodeFileManifestDto? uploaded = await TryUploadWithConflictHandlingAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local,
                    existingRemoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            if (uploaded is null)
            {
                return;
            }

            string localContentHash = ResolveUploadedLocalContentHash(local, uploaded);
            local.ContentHash = localContentHash;
            await _stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, localContentHash, local.LastWriteUtc, local.SizeBytes, uploaded),
                    cancellationToken)
                .ConfigureAwait(false);
            Report(result, options, SyncActivityKind.Uploaded, relativePath, null);
        }

        private async Task<NodeFileManifestDto?> TryUploadWithConflictHandlingAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto? existingRemoteFile,
            CancellationToken cancellationToken)
        {
            try
            {
                return await UploadFileWithProgressAsync(
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
                return null;
            }
            catch (HttpRequestException exception) when (existingRemoteFile is null && IsRemoteConflict(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await FindLatestRemoteFileAsync(
                        syncPair,
                        relativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (latestRemoteFile is null)
                {
                    throw;
                }

                return await ResolveRemoteCreateConflictAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        latestRemoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LocalFileUnavailableException exception)
            {
                Report(result, options, SyncActivityKind.Skipped, relativePath, exception.Reason);
                result.RecordDeferredLocalPath(relativePath);
                return null;
            }
        }

        private async Task<NodeFileManifestDto?> ResolveRemoteCreateConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto latestRemoteFile,
            CancellationToken cancellationToken)
        {
            bool contentMatches = ContentMatches(local.ContentHash, latestRemoteFile.ContentHash)
                && local.SizeBytes == latestRemoteFile.SizeBytes;
            if (!contentMatches)
            {
                await PreserveConflictAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        latestRemoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            _logger.LogInformation(
                "Remote file create for {RelativePath} hit conflict after matching content was committed; reusing file {RemoteFileId}.",
                relativePath,
                latestRemoteFile.Id);
            return latestRemoteFile;
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
            LocalDirectorySnapshot localDirectory,
            DirectoryContentIndex localDirectoryContentIndex,
            CancellationToken cancellationToken)
        {
            if (localDirectoryContentIndex.HasChildren(relativePath)
                || DirectoryHasFileSystemEntries(localDirectory.FullPath))
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
                details = await PreserveDivergedConflictAsync(
                        syncPair,
                        options,
                        relativePath,
                        local,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (local is not null)
            {
                details = await PreserveRemoteDeletionConflictAsync(
                        syncPair,
                        options,
                        relativePath,
                        local,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (remoteFile is not null)
            {
                details = await PreserveLocalDeletionConflictAsync(
                        syncPair,
                        options,
                        relativePath,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Report(result, options, SyncActivityKind.Conflict, relativePath, details);
        }

        private async Task<string> PreserveDivergedConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            await EnsureLocalContentHashAsync(local, options, cancellationToken).ConfigureAwait(false);
            string conflictPath = _localWriter.CreateConflictRelativePath(
                syncPair.LocalRootPath,
                relativePath,
                DateTime.UtcNow);
            EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, conflictPath, remoteFile.SizeBytes);
            await WriteMaterializedRemoteFileAsync(
                    syncPair,
                    options,
                    conflictPath,
                    relativePath,
                    remoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            await _stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remoteFile),
                    cancellationToken)
                .ConfigureAwait(false);
            return "Remote version saved as " + conflictPath;
        }

        private async Task<string> PreserveRemoteDeletionConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            LocalFileSnapshot local,
            CancellationToken cancellationToken)
        {
            NodeFileManifestDto uploaded = await UploadFileWithProgressAsync(
                    syncPair.RemoteRootNodeId,
                    relativePath,
                    local,
                    null,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            string localContentHash = ResolveUploadedLocalContentHash(local, uploaded);
            local.ContentHash = localContentHash;
            await _stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, localContentHash, local.LastWriteUtc, local.SizeBytes, uploaded),
                    cancellationToken)
                .ConfigureAwait(false);
            return "Remote deletion conflicted with local change; local version was uploaded again.";
        }

        private async Task<string> PreserveLocalDeletionConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, relativePath, remoteFile.SizeBytes);
            await WriteRemoteFileAfterLocalDeletionAsync(
                    syncPair,
                    options,
                    relativePath,
                    relativePath,
                    remoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            await _stateStore.UpsertAsync(
                    BuildBaseline(
                        syncPair,
                        relativePath,
                        remoteFile.ContentHash,
                        remoteFile.UpdatedAt,
                        remoteFile.SizeBytes,
                        remoteFile),
                    cancellationToken)
                .ConfigureAwait(false);
            return "Local deletion conflicted with remote change; remote version was restored locally.";
        }

        private async Task WriteMaterializedRemoteFileAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetRelativePath,
            string remoteRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            RemoteFileMaterializationRequest? request = await PrepareRemoteFileMaterializationAsync(
                syncPair,
                targetRelativePath,
                remoteFile,
                cancellationToken).ConfigureAwait(false);

            await WriteRemoteFileContentAsync(
                    syncPair,
                    options,
                    targetRelativePath,
                    remoteRelativePath,
                    remoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request is not null)
            {
                await _remoteFileMaterializationObserver!.AfterWriteFileAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task WriteRemoteFileAfterLocalDeletionAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetRelativePath,
            string remoteRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            await PrepareRemoteFileMaterializationAsync(
                syncPair,
                targetRelativePath,
                remoteFile,
                cancellationToken).ConfigureAwait(false);

            await WriteRemoteFileContentAsync(
                    syncPair,
                    options,
                    targetRelativePath,
                    remoteRelativePath,
                    remoteFile,
                cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<RemoteFileMaterializationRequest?> PrepareRemoteFileMaterializationAsync(
            SyncPair syncPair,
            string targetRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            RemoteFileMaterializationRequest? request = CreateRemoteFileMaterializationRequest(
                syncPair,
                targetRelativePath,
                remoteFile);
            if (request is not null)
            {
                await _remoteFileMaterializationObserver!.BeforeWriteFileAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }

            return request;
        }

        private async Task WriteRemoteFileContentAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetRelativePath,
            string remoteRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            await _localWriter.WriteFileAsync(
                    syncPair.LocalRootPath,
                    targetRelativePath,
                    (stream, token) => DownloadAndVerifyFileAsync(remoteFile, remoteRelativePath, options, stream, token),
                    remoteFile.UpdatedAt == default ? null : remoteFile.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private RemoteFileMaterializationRequest? CreateRemoteFileMaterializationRequest(
            SyncPair syncPair,
            string relativePath,
            NodeFileManifestDto remoteFile)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || _remoteFileMaterializationObserver is null)
            {
                return null;
            }

            return new RemoteFileMaterializationRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                relativePath,
                remoteFile);
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
            if (_remotePathLookupCrawler is null)
            {
                throw new InvalidOperationException("Remote mutation recovery requires path lookup capability.");
            }

            string normalizedPath = SyncPath.Normalize(relativePath);
            RemoteTreeLookupSnapshot latestTree = await _remotePathLookupCrawler
                .CrawlPathLookupsAsync(
                    syncPair.RemoteRootNodeId,
                    [normalizedPath],
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            string key = SyncPath.ToKey(relativePath);
            return latestTree.FilesByPath.TryGetValue(key, out RemoteFileSnapshot? remoteFile)
                ? remoteFile.File
                : null;
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
                RemoteFileManifestId = remoteFile?.FileManifestId,
                RemoteOriginalNodeFileId = remoteFile?.OriginalNodeFileId,
                RemoteContentHash = remoteFile?.ContentHash,
                RemoteETag = remoteFile?.ETag,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private static SyncStateEntry BuildHydratedPlaceholderBaseline(
            SyncPair syncPair,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            SyncStateEntry existingState)
        {
            SyncStateEntry baseline = BuildBaseline(
                syncPair,
                relativePath,
                local.ContentHash,
                local.LastWriteUtc,
                local.SizeBytes,
                remoteFile);
            baseline.PlaceholderIdentity = existingState.PlaceholderIdentity;
            baseline.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            return baseline;
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

            bool materialized = hydrationState == SyncPlaceholderHydrationState.Hydrated;

            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(relativePath),
                Kind = SyncEntryKind.File,
                LocalContentHash = materialized ? remoteFile.ContentHash : null,
                LocalLastWriteUtc = materialized
                    ? placeholder.LocalLastWriteUtc?.ToUniversalTime() ?? remoteFile.UpdatedAt.ToUniversalTime()
                    : null,
                LocalSizeBytes = materialized ? placeholder.LocalSizeBytes ?? remoteFile.SizeBytes : null,
                RemoteSizeBytes = remoteFile.SizeBytes,
                RemoteFileId = remoteFile.Id,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileManifestId = remoteFile.FileManifestId,
                RemoteOriginalNodeFileId = remoteFile.OriginalNodeFileId,
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

        private static bool IsSameOrDescendantPathKey(string pathKey, string directoryKey)
        {
            return PathComparer.Equals(pathKey, directoryKey)
                || pathKey.StartsWith(directoryKey.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
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

        private static bool DateTimesMatchWithinCloudFilesMetadataTolerance(DateTime left, DateTime right)
        {
            TimeSpan difference = left.ToUniversalTime() - right.ToUniversalTime();
            return difference.Duration() <= CloudFilesMetadataTimestampTolerance;
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

            if (options.ApprovedRemoteDeleteCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Approved remote delete count must be positive.");
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

            if (TryCalculateOnlineOnlyPlaceholderTransferBytes(syncPair, state, local, remote, out transferBytes))
            {
                return transferBytes > 0;
            }

            if (local is not null && remote is not null && ContentMatches(local.ContentHash, remote.File.ContentHash))
            {
                transferBytes = 0;
                return false;
            }

            SyncFileChangeKind changeKind = ResolveTrackedFileChange(CreateFileChangeState(state, local, remote));
            return TryCalculateTrackedTransferBytes(changeKind, local, remote, out transferBytes);
        }

        private static bool TryCalculateOnlineOnlyPlaceholderTransferBytes(
            SyncPair syncPair,
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (local is null || !IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                transferBytes = 0;
                return false;
            }

            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            transferBytes = remoteChanged ? remote!.File.SizeBytes : 0;
            return true;
        }

        private static bool TryCalculateTrackedTransferBytes(
            SyncFileChangeKind changeKind,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            switch (changeKind)
            {
                case SyncFileChangeKind.Upload:
                    transferBytes = local!.SizeBytes;
                    return true;
                case SyncFileChangeKind.Download:
                    transferBytes = remote!.File.SizeBytes;
                    return true;
                case SyncFileChangeKind.Conflict:
                    return TryCalculateConflictTransferBytes(local, remote?.File, out transferBytes);
                case SyncFileChangeKind.None:
                case SyncFileChangeKind.DeleteState:
                case SyncFileChangeKind.DeleteLocal:
                case SyncFileChangeKind.DeleteRemote:
                    transferBytes = 0;
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null);
            }
        }

        private static bool TryCalculateUntrackedTransferBytes(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (local is null)
            {
                return TryCalculateRemoteOnlyTransferBytes(syncPair, remote, out transferBytes);
            }

            if (remote is null)
            {
                transferBytes = local.SizeBytes;
                return true;
            }

            if (IsUntrackedRemoteReplacement(local, remote))
            {
                transferBytes = remote.File.SizeBytes;
                return true;
            }

            transferBytes = 0;
            return false;
        }

        private static bool TryCalculateRemoteOnlyTransferBytes(
            SyncPair syncPair,
            RemoteFileSnapshot? remote,
            out long transferBytes)
        {
            if (remote is null || syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                transferBytes = 0;
                return false;
            }

            transferBytes = remote.File.SizeBytes;
            return true;
        }

        private static bool IsUntrackedRemoteReplacement(
            LocalFileSnapshot local,
            RemoteFileSnapshot remote)
        {
            return !string.IsNullOrWhiteSpace(local.ContentHash)
                && !ContentMatches(local.ContentHash, remote.File.ContentHash);
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
            relativePath = ResolvePlannedTransferRelativePath(key, local, remote, state);

            if (state is null)
            {
                return TryCreateRemoteOnlyDownload(syncPair, local, remote, out downloadBytes, out replacedLocalBytes);
            }

            if (TryCreateOnlineOnlyPlaceholderDownload(
                    syncPair,
                    state,
                    local,
                    remote,
                    out downloadBytes,
                    out replacedLocalBytes))
            {
                return downloadBytes > 0;
            }

            if (LocalAndRemoteContentMatch(local, remote))
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            SyncFileChangeKind changeKind = ResolveTrackedFileChange(CreateFileChangeState(state, local, remote));
            return TryCreateTrackedLocalDownload(changeKind, local, remote, out downloadBytes, out replacedLocalBytes);
        }

        private static string ResolvePlannedTransferRelativePath(
            string key,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            SyncStateEntry? state)
        {
            if (local is not null)
            {
                return local.RelativePath;
            }

            if (remote is not null)
            {
                return remote.RelativePath;
            }

            return state is not null ? state.RelativePath : key;
        }

        private static bool LocalAndRemoteContentMatch(
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return local is not null
                && remote is not null
                && ContentMatches(local.ContentHash, remote.File.ContentHash);
        }

        private static bool TryCreateOnlineOnlyPlaceholderDownload(
            SyncPair syncPair,
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            if (local is null || !IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, state))
            {
                downloadBytes = 0;
                replacedLocalBytes = 0;
                return false;
            }

            bool remoteChanged = remote is not null && !RemoteMatchesBaseline(remote.File, state);
            if (remoteChanged)
            {
                downloadBytes = remote!.File.SizeBytes;
            }
            else
            {
                downloadBytes = 0;
            }

            replacedLocalBytes = 0;
            return true;
        }

        private static bool TryCreateTrackedLocalDownload(
            SyncFileChangeKind changeKind,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            out long downloadBytes,
            out long replacedLocalBytes)
        {
            switch (changeKind)
            {
                case SyncFileChangeKind.Download:
                    downloadBytes = remote!.File.SizeBytes;
                    replacedLocalBytes = local?.SizeBytes ?? 0;
                    return true;
                case SyncFileChangeKind.Conflict:
                    return TryCreateConflictDownload(remote, out downloadBytes, out replacedLocalBytes);
                case SyncFileChangeKind.None:
                case SyncFileChangeKind.DeleteState:
                case SyncFileChangeKind.DeleteLocal:
                case SyncFileChangeKind.DeleteRemote:
                case SyncFileChangeKind.Upload:
                    downloadBytes = 0;
                    replacedLocalBytes = 0;
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null);
            }
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
            DirectoryContentIndex remoteDirectoryContentIndex,
            IReadOnlySet<string>? scopedFileDeleteKeys,
            IReadOnlySet<string>? scopedDirectoryDeleteKeys,
            IReadOnlySet<string> scopedLocalDeletedFileKeys,
            ScopedVirtualFilesDirectoryDeletePlan? scopedDirectoryDelete)
        {
            if (stateByPath.Count == 0 && directoryStateByPath.Count == 0)
            {
                return new SyncDeleteGuard(options, plannedLocalDeletes: 0, plannedRemoteDeletes: 0);
            }

            (int LocalDeletes, int RemoteDeletes) fileDeletes = CountPlannedFileDeletes(
                stateByPath,
                localByPath,
                remoteByPath,
                scopedFileDeleteKeys,
                scopedLocalDeletedFileKeys);
            (int LocalDeletes, int RemoteDeletes) directoryDeletes = CountPlannedDirectoryDeletes(
                directoryStateByPath,
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex,
                scopedDirectoryDeleteKeys,
                scopedDirectoryDelete);
            int scopedRemoteDirectoryDeletes = scopedDirectoryDelete?.DirectoryKeys.Count ?? 0;
            return new SyncDeleteGuard(
                options,
                fileDeletes.LocalDeletes + directoryDeletes.LocalDeletes,
                fileDeletes.RemoteDeletes + directoryDeletes.RemoteDeletes + scopedRemoteDirectoryDeletes);
        }

        private static (int LocalDeletes, int RemoteDeletes) CountPlannedFileDeletes(
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlySet<string>? scopedFileDeleteKeys,
            IReadOnlySet<string> scopedLocalDeletedFileKeys)
        {
            int localDeletes = 0;
            int remoteDeletes = 0;
            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                localByPath.TryGetValue(state.Key, out LocalFileSnapshot? local);
                remoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote);
                SyncDeleteDirection direction = GetPlannedDeleteDirection(
                    state.Value,
                    local,
                    remote,
                    scopedLocalDeletedFileKeys.Contains(state.Key));
                CountScopedDelete(direction, scopedFileDeleteKeys, state.Key, ref localDeletes, ref remoteDeletes);
            }

            return (localDeletes, remoteDeletes);
        }

        private static (int LocalDeletes, int RemoteDeletes) CountPlannedDirectoryDeletes(
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            DirectoryContentIndex localDirectoryContentIndex,
            DirectoryContentIndex remoteDirectoryContentIndex,
            IReadOnlySet<string>? scopedDirectoryDeleteKeys,
            ScopedVirtualFilesDirectoryDeletePlan? scopedDirectoryDelete)
        {
            int localDeletes = 0;
            int remoteDeletes = 0;
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (scopedDirectoryDelete?.DirectoryKeys.Contains(state.Key, PathComparer) == true)
                {
                    continue;
                }

                localDirectoriesByPath.TryGetValue(state.Key, out LocalDirectorySnapshot? local);
                remoteDirectoriesByPath.TryGetValue(state.Key, out RemoteDirectorySnapshot? remote);
                SyncDeleteDirection direction = GetPlannedDirectoryDeleteDirection(
                    state.Value,
                    local,
                    remote,
                    remoteDirectoryContentIndex);
                CountScopedDelete(
                    direction,
                    scopedDirectoryDeleteKeys,
                    state.Key,
                    ref localDeletes,
                    ref remoteDeletes);
            }

            return (localDeletes, remoteDeletes);
        }

        private static void CountScopedDelete(
            SyncDeleteDirection direction,
            IReadOnlySet<string>? scopedDeleteKeys,
            string pathKey,
            ref int localDeletes,
            ref int remoteDeletes)
        {
            if (!IsScopedDeleteAllowed(scopedDeleteKeys, pathKey))
            {
                return;
            }

            switch (direction)
            {
                case SyncDeleteDirection.None:
                    return;
                case SyncDeleteDirection.Local:
                    localDeletes++;
                    return;
                case SyncDeleteDirection.Remote:
                    remoteDeletes++;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
            }
        }

        private static bool IsScopedDeleteAllowed(IReadOnlySet<string>? scopedDeleteKeys, string pathKey)
        {
            return scopedDeleteKeys is null || scopedDeleteKeys.Contains(pathKey);
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
            DirectoryContentIndex remoteDirectoryContentIndex)
        {
            if (state.RemoteNodeId is null)
            {
                return SyncDeleteDirection.None;
            }

            if (local is null)
            {
                return GetMissingLocalDirectoryDeleteDirection(
                    state,
                    remote,
                    remoteDirectoryContentIndex);
            }

            return remote is null ? SyncDeleteDirection.Local : SyncDeleteDirection.None;
        }

        private static SyncDeleteDirection GetMissingLocalDirectoryDeleteDirection(
            SyncStateEntry state,
            RemoteDirectorySnapshot? remote,
            DirectoryContentIndex remoteDirectoryContentIndex)
        {
            if (remote is null)
            {
                return SyncDeleteDirection.None;
            }

            string relativePath = ResolveDirectoryRelativePath(state.RelativePath, null, remote, state);
            return remoteDirectoryContentIndex.HasChildren(relativePath)
                ? SyncDeleteDirection.None
                : SyncDeleteDirection.Remote;
        }

        private static SyncDeleteDirection GetPlannedDeleteDirection(
            SyncStateEntry? state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            bool exactLocalDelete)
        {
            if (state is null || (local is null && remote is null))
            {
                return SyncDeleteDirection.None;
            }

            if (IsMissingOnlineOnlyPlaceholder(state, local, remote))
            {
                return exactLocalDelete ? SyncDeleteDirection.Remote : SyncDeleteDirection.None;
            }

            if (LocalAndRemoteContentMatches(local, remote))
            {
                return SyncDeleteDirection.None;
            }

            return ToDeleteDirection(ResolveTrackedFileChange(CreateFileChangeState(state, local, remote)));
        }

        private static bool IsMissingOnlineOnlyPlaceholder(
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return local is null && remote is not null && IsOnlineOnlyPlaceholderState(state);
        }

        private static bool LocalAndRemoteContentMatches(
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return local is not null
                && remote is not null
                && ContentMatches(local.ContentHash, remote.File.ContentHash);
        }

        private static SyncDeleteDirection ToDeleteDirection(SyncFileChangeKind changeKind)
        {
            return changeKind switch
            {
                SyncFileChangeKind.DeleteLocal => SyncDeleteDirection.Local,
                SyncFileChangeKind.DeleteRemote => SyncDeleteDirection.Remote,
                SyncFileChangeKind.None => SyncDeleteDirection.None,
                SyncFileChangeKind.DeleteState => SyncDeleteDirection.None,
                SyncFileChangeKind.Upload => SyncDeleteDirection.None,
                SyncFileChangeKind.Download => SyncDeleteDirection.None,
                SyncFileChangeKind.Conflict => SyncDeleteDirection.None,
                _ => throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null)
            };
        }

        private static SyncFileChangeState CreateFileChangeState(
            SyncStateEntry state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote)
        {
            return new SyncFileChangeState(
                LocalDeleted: local is null && !string.IsNullOrWhiteSpace(state.LocalContentHash),
                RemoteDeleted: remote is null && state.RemoteFileId.HasValue,
                LocalChanged: local is not null && !ContentMatches(local.ContentHash, state.LocalContentHash),
                RemoteChanged: remote is not null && !RemoteMatchesBaseline(remote.File, state),
                BaselineDiverged: !ContentMatches(state.LocalContentHash, state.RemoteContentHash));
        }

        private static SyncFileChangeKind ResolveTrackedFileChange(SyncFileChangeState changeState)
        {
            if (changeState.BaselineDiverged)
            {
                return changeState.HasChanges ? SyncFileChangeKind.Conflict : SyncFileChangeKind.None;
            }

            return (changeState.LocalDeleted, changeState.RemoteDeleted, changeState.LocalChanged, changeState.RemoteChanged) switch
            {
                (false, false, false, false) => SyncFileChangeKind.None,
                (true, true, false, false) => SyncFileChangeKind.DeleteState,
                (false, true, false, false) => SyncFileChangeKind.DeleteLocal,
                (true, false, false, false) => SyncFileChangeKind.DeleteRemote,
                (false, false, true, false) => SyncFileChangeKind.Upload,
                (false, false, false, true) => SyncFileChangeKind.Download,
                _ => SyncFileChangeKind.Conflict
            };
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
            return InitialVirtualFilesPlaceholderPolicy.IsOnlineOnly(state);
        }

        private static bool IsIncompleteOnlineOnlyPlaceholderBaseline(SyncStateEntry state)
        {
            return state.Kind == SyncEntryKind.File
                && (state.PlaceholderHydrationState == SyncPlaceholderHydrationState.RemoteOnly
                    || state.PlaceholderHydrationState == SyncPlaceholderHydrationState.Dehydrated)
                && state.PlaceholderIdentity is not { Length: > 0 }
                && HasRemoteFileBaseline(state);
        }

        private static bool HasRemoteFileBaseline(SyncStateEntry state)
        {
            return InitialVirtualFilesPlaceholderPolicy.HasRemoteBaseline(state);
        }

        private static bool IsOnlineOnlyPlaceholderState(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsOnlineOnly(baseline);
        }

        private static bool IsVirtualFilesResumeCandidateState(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.IsResumeCandidate(baseline);
        }

        private static bool HasRemoteFileBaseline(InitialVirtualFilesPlaceholderBaseline baseline)
        {
            return InitialVirtualFilesPlaceholderPolicy.HasRemoteBaseline(baseline);
        }

        private static bool ContentMatches(string? left, string? right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldFinalizeConvergedLocalFile(SyncPair syncPair, LocalFileSnapshot local)
        {
            return syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && !local.IsCloudFilesOnlineOnlyPlaceholder;
        }

        private static SyncRunOptions CloneWithoutRunProgress(SyncRunOptions options)
        {
            return new SyncRunOptions
            {
                Scope = options.Scope,
                DeleteRemotePermanently = options.DeleteRemotePermanently,
                MinimumLocalUploadAge = options.MinimumLocalUploadAge,
                MaximumLocalDeletesPerRun = options.MaximumLocalDeletesPerRun,
                MaximumRemoteDeletesPerRun = options.MaximumRemoteDeletesPerRun,
                ApprovedRemoteDeleteCount = options.ApprovedRemoteDeleteCount,
                MaximumStoredResultActivities = options.MaximumStoredResultActivities,
                InitialVirtualFilesPopulationQueueCapacity = options.InitialVirtualFilesPopulationQueueCapacity,
                InitialVirtualFilesStateBatchSize = options.InitialVirtualFilesStateBatchSize,
                InitialVirtualFilesPlaceholderConcurrency = options.InitialVirtualFilesPlaceholderConcurrency,
                InitialVirtualFilesPlaceholderBatchSize = options.InitialVirtualFilesPlaceholderBatchSize,
                ActivityProgress = options.ActivityProgress,
                TransferProgress = options.TransferProgress,
                CooperativeYieldAsync = options.CooperativeYieldAsync,
            };
        }

        private static bool DirectoryHasFileSystemEntries(string fullPath)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(fullPath).Any();
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
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

        private static bool IsRemoteConflict(HttpRequestException exception)
        {
            return exception.StatusCode == HttpStatusCode.Conflict;
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

        private static bool ShouldIncludeScopedDirectoryDescendants(SyncPair syncPair)
        {
            return syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles;
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

            if (local.IsCloudFilesOnlineOnlyPlaceholder
                && (IsOnlineOnlyPlaceholderState(state) || IsIncompleteOnlineOnlyPlaceholderBaseline(state)))
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

        private static IReadOnlySet<string> BuildExactScopedPathKeys(IEnumerable<string> relativePaths)
        {
            HashSet<string> keys = new(PathComparer);
            foreach (string relativePath in relativePaths)
            {
                string normalizedPath = SyncPath.Normalize(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedPath) || SyncPathIgnoreRules.ShouldIgnore(normalizedPath))
                {
                    continue;
                }

                keys.Add(SyncPath.ToKey(normalizedPath));
            }

            return keys;
        }

        private static IReadOnlySet<string> AddScopedPathKeys(
            IReadOnlySet<string> existingKeys,
            IEnumerable<string> additionalKeys)
        {
            HashSet<string> keys = new(existingKeys, PathComparer);
            keys.UnionWith(additionalKeys);
            return keys;
        }

        private async Task LogInitialVirtualFilesPopulationHeartbeatAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            Stopwatch stopwatch,
            InitialVirtualFilesPopulationMetrics metrics,
            CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(InitialVirtualFilesHeartbeatLogInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int createdPlaceholders = metrics.CreatedPlaceholders;
                double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d);
                int discoveredDirectoryCount = metrics.DiscoveredDirectories;
                int discoveredFileCount = metrics.DiscoveredFiles;
                int stateFileRowsWritten = metrics.StateFileRowsWritten;
                int stateDirectoryRowsWritten = metrics.StateDirectoryRowsWritten;
                double discoveredDirectoryRatePerSecond = discoveredDirectoryCount / elapsedSeconds;
                double discoveredFileRatePerSecond = discoveredFileCount / elapsedSeconds;
                double createdPlaceholderRatePerSecond = createdPlaceholders / elapsedSeconds;
                double stateWriteRatePerSecond = (stateFileRowsWritten + stateDirectoryRowsWritten) / elapsedSeconds;
                RemoteTreeScanProgressCounter remoteScanProgress = metrics.RemoteScanProgress;
                int remotePageCount = remoteScanProgress.PagesScanned;
                double remotePageAverageLatencyMilliseconds = remotePageCount <= 0
                    ? 0d
                    : remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds / remotePageCount;
                long managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
                metrics.RecordManagedHeapSample(managedHeapBytes);
                _logger.LogInformation(
                    "Initial streaming Windows virtual-files population heartbeat for pair {SyncPairId}: elapsed={ElapsedMilliseconds} ms; discovered directories={DirectoryCount} at {DirectoryDiscoveryRatePerSecond:F2} dirs/sec, files={FileCount} at {FileDiscoveryRatePerSecond:F2} files/sec; completed directories={CompletedDirectoryCount}, files={CompletedFileCount}; remote pages read={RemotePageCount}, remote page latency total={RemotePageLatencyTotalMilliseconds:F0} ms, avg={RemotePageLatencyAverageMilliseconds:F2} ms, max={RemotePageLatencyMaxMilliseconds:F0} ms, last={RemotePageLatencyLastMilliseconds:F0} ms; placeholders created or refreshed={CreatedPlaceholderCount}, current skipped={SkippedCurrentPlaceholderCount}, user-action skipped={SkippedUnavailablePlaceholderCount}, rate={CreatedPlaceholderRatePerSecond:F2} placeholders/sec; state writes file rows={StateFileRowsWritten}, file batches={StateFileWriteBatchCount}, directory rows={StateDirectoryRowsWritten}, state write rate={StateWriteRatePerSecond:F2} rows/sec; managed heap={ManagedHeapBytes} bytes; queue capacity={QueueCapacity}, placeholder concurrency={PlaceholderConcurrency}, placeholder batch size={PlaceholderBatchSize}, state batch size={StateBatchSize}.",
                    syncPair.SyncPairId,
                    stopwatch.ElapsedMilliseconds,
                    discoveredDirectoryCount,
                    discoveredDirectoryRatePerSecond,
                    discoveredFileCount,
                    discoveredFileRatePerSecond,
                    metrics.CompletedDirectories,
                    metrics.CompletedFiles,
                    remotePageCount,
                    remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds,
                    remotePageAverageLatencyMilliseconds,
                    remoteScanProgress.PageReadLatencyMax.TotalMilliseconds,
                    remoteScanProgress.LastPageReadLatency.TotalMilliseconds,
                    createdPlaceholders,
                    metrics.SkippedCurrentPlaceholders,
                    metrics.SkippedUnavailablePlaceholders,
                    createdPlaceholderRatePerSecond,
                    stateFileRowsWritten,
                    metrics.StateFileWriteBatches,
                    stateDirectoryRowsWritten,
                    stateWriteRatePerSecond,
                    managedHeapBytes,
                    options.InitialVirtualFilesPopulationQueueCapacity,
                    options.InitialVirtualFilesPlaceholderConcurrency,
                    options.InitialVirtualFilesPlaceholderBatchSize,
                    options.InitialVirtualFilesStateBatchSize);
            }
        }

        private static async Task IgnoreExpectedHeartbeatCancellationAsync(
            Task heartbeat,
            CancellationToken cancellationToken)
        {
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private class InitialVirtualFilesRemoteProgressReporter : IProgress<RemoteTreeScanProgress>
        {
            private readonly IProgress<RemoteTreeScanProgress> _inner;
            private readonly SyncRunOptions _options;
            private readonly DateTime _startedAtUtc;
            private readonly bool _publishRunProgress;
            private readonly InitialVirtualFilesPopulationMetrics _metrics;

            public InitialVirtualFilesRemoteProgressReporter(
                IProgress<RemoteTreeScanProgress> inner,
                SyncRunOptions options,
                DateTime startedAtUtc,
                bool publishRunProgress,
                InitialVirtualFilesPopulationMetrics metrics)
            {
                _inner = inner;
                _options = options;
                _startedAtUtc = startedAtUtc;
                _publishRunProgress = publishRunProgress;
                _metrics = metrics;
            }

            public void Report(RemoteTreeScanProgress value)
            {
                ArgumentNullException.ThrowIfNull(value);
                _inner.Report(value);
                if (!_publishRunProgress)
                {
                    return;
                }

                int itemsDiscovered = GetInitialVirtualFilesItemCount(value.FilesScanned, value.DirectoriesScanned);
                if (itemsDiscovered == 0)
                {
                    return;
                }

                int itemsCompleted = GetInitialVirtualFilesItemCount(
                    _metrics.CompletedFiles,
                    _metrics.CompletedDirectories);
                int knownItemsTotal = value.EntriesExpected.GetValueOrDefault(itemsDiscovered);
                int itemsTotal = Math.Max(itemsCompleted, Math.Max(itemsDiscovered, knownItemsTotal));
                ReportRunProgress(
                    _options,
                    SyncRunProgressStage.CreatingPlaceholders,
                    itemsCompleted,
                    itemsTotal,
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
