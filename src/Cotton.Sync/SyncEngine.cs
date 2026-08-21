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
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncDeletePlanner;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;
using static Cotton.Sync.SyncRunProgressReporter;
using static Cotton.Sync.SyncTransferPlanner;

namespace Cotton.Sync
{
    /// <summary>
    /// Reconciles local and remote file snapshots for one synchronization pair.
    /// </summary>
    public class SyncEngine : ISyncEngine
    {
        private static readonly TimeSpan InitialVirtualFilesHeartbeatLogInterval = TimeSpan.FromSeconds(30);
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
        private readonly SyncDirectoryReconciler _directoryReconciler;
        private readonly SyncDirectoryDeleteReconciler _directoryDeleteReconciler;
        private readonly SyncLocalContentHashResolver _contentHashResolver;
        private readonly RemoteDirectoryMoveCoordinator _remoteDirectoryMoveCoordinator;

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
            _directoryReconciler = new SyncDirectoryReconciler(
                _remoteDirectories,
                _stateStore,
                _localWriter,
                _remoteDirectoryMaterializationObserver,
                _logger);
            _directoryDeleteReconciler = new SyncDirectoryDeleteReconciler(
                _remoteDirectories,
                _stateStore,
                _localWriter);
            _contentHashResolver = new SyncLocalContentHashResolver(
                _localContentHasher,
                _localContentHashProgressHasher);
            RemoteDirectoryMovePlanner remoteDirectoryMovePlanner = new(_logger);
            _remoteDirectoryMoveCoordinator = new RemoteDirectoryMoveCoordinator(
                remoteDirectoryMovePlanner,
                _localWriter,
                _stateStore,
                _remoteDirectoryTreePopulationObserver,
                _remoteFilePlaceholderWriter,
                _contentHashResolver);
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
            SyncRunOptionsValidator.Validate(runOptions);
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
            await _remoteDirectoryMoveCoordinator.CoalesceAsync(
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
            await _directoryReconciler.ReconcileWithoutBaselineAsync(directoryReconciliation).ConfigureAwait(false);
            return directoryPathKeys;
        }

        private async Task<SyncDeletePlan> BuildSyncDeletePlanAsync(SyncRunContext context)
        {
            await EnsureLocalContentHashesForStateFilesAsync(
                    context.LocalFilesByPath,
                    context.FileStateByPath,
                    context.Options,
                    context.Result,
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
            await _directoryDeleteReconciler.ReconcileAsync(directoryDeletes).ConfigureAwait(false);
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
            IReadOnlyDictionary<SyncRunProgressStage, int> fileCountsByStage = CountFileRunProgressStages(
                context,
                pathKeys);
            foreach (string key in pathKeys)
            {
                await ReconcileSyncFileAsync(context, deletePlan, progress, fileCountsByStage, pathKeys.Count, key)
                    .ConfigureAwait(false);
            }

            return new SyncFilePhaseResult(pathKeys, progress.FilesCompleted, plannedTransferBytesTotal);
        }

        private async Task ReconcileSyncFileAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            SyncFileReconciliationProgress progress,
            IReadOnlyDictionary<SyncRunProgressStage, int> fileCountsByStage,
            int fileCount,
            string pathKey)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.LocalFilesByPath.TryGetValue(pathKey, out LocalFileSnapshot? local);
            context.RemoteFilesByPath.TryGetValue(pathKey, out RemoteFileSnapshot? remote);
            context.FileStateByPath.TryGetValue(pathKey, out SyncStateEntry? state);
            string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state?.RelativePath ?? pathKey;
            SyncRunProgressStage progressStage = ResolveFileRunProgressStage(context.SyncPair, local, remote, state);
            int stageFileCount = fileCountsByStage[progressStage];
            long plannedTransferBytes = CalculatePlannedTransferBytes(
                context.SyncPair,
                pathKey,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            ReportSyncFileProgress(context, progress, progressStage, stageFileCount, relativePath);
            if (!context.Result.IsLocalPathDeferred(relativePath))
            {
                try
                {
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
                }
                catch (LocalFileUnavailableException exception)
                {
                    ReportUnavailableLocalFile(context.Result, context.Options, relativePath, exception);
                }
            }

            progress.CompleteFile(progressStage, plannedTransferBytes);
            ReportSyncFileProgress(context, progress, progressStage, stageFileCount, relativePath);
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
            DateTime? lastReportedAtUtc = progress.GetLastReportedAtUtc(stage);
            ReportItemRunProgress(
                context.Options,
                stage,
                progress.GetFilesCompleted(stage),
                fileCount,
                relativePath,
                context.StartedAtUtc,
                ref lastReportedAtUtc,
                bytesCompleted: progress.CompletedTransferBytes,
                bytesTotal: progress.PlannedTransferBytesTotal);
            progress.SetLastReportedAtUtc(stage, lastReportedAtUtc);
        }

        private static IReadOnlyDictionary<SyncRunProgressStage, int> CountFileRunProgressStages(
            SyncRunContext context,
            IReadOnlyList<string> pathKeys)
        {
            Dictionary<SyncRunProgressStage, int> fileCountsByStage = [];
            foreach (string pathKey in pathKeys)
            {
                context.LocalFilesByPath.TryGetValue(pathKey, out LocalFileSnapshot? local);
                context.RemoteFilesByPath.TryGetValue(pathKey, out RemoteFileSnapshot? remote);
                context.FileStateByPath.TryGetValue(pathKey, out SyncStateEntry? state);
                SyncRunProgressStage stage = ResolveFileRunProgressStage(context.SyncPair, local, remote, state);
                fileCountsByStage[stage] = fileCountsByStage.GetValueOrDefault(stage) + 1;
            }

            return fileCountsByStage;
        }

        private async Task CompleteSyncRunAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            IReadOnlyList<string> directoryPathKeys,
            SyncFilePhaseResult filePhase)
        {
            if (deletePlan.HasLocalDirectoryDeleteCandidates)
            {
                await _directoryDeleteReconciler.ReconcileEmptyLocalDirectoriesAsync(
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
            SyncRunResult result,
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
                        try
                        {
                            await EnsureLocalContentHashForBaselineComparisonAsync(
                                    local,
                                    state.Value,
                                    options,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (LocalFileUnavailableException exception)
                        {
                            ReportUnavailableLocalFile(result, options, local.RelativePath, exception);
                        }
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
            InitialVirtualFilesStateFirstInspection inspection = new InitialVirtualFilesStateFirstInspection();
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
            RecordInitialVirtualFilesRemotePath(state, directory.RelativePath);
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
            await _directoryReconciler.CreateRemoteBackedLocalDirectoryAsync(
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

            RemoteDirectoryMaterializationRequest request =
                SyncDirectoryReconciler.CreateRemoteDirectoryMaterializationRequest(
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
            string fileKey = RecordInitialVirtualFilesRemotePath(state, file.RelativePath);
            state.StreamedRemoteFileKeys.Add(fileKey);
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

        private static string RecordInitialVirtualFilesRemotePath(
            InitialVirtualFilesConsumerState state,
            string relativePath)
        {
            string normalizedPath = SyncPath.Normalize(relativePath);
            string pathKey = SyncPath.ToKey(normalizedPath);
            if (state.StreamedRemotePathByKey.TryGetValue(pathKey, out string? existingPath))
            {
                throw new SyncPathCollisionException(existingPath, normalizedPath);
            }

            state.StreamedRemotePathByKey.Add(pathKey, normalizedPath);
            return pathKey;
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

            SyncDeleteGuard deleteGuard = new(options, plannedLocalDeletes: missingBaselines.Count, []);
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

                    InitialVirtualFilesFileWorkResult[] results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
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
            RemoteFilePlaceholderRequest[] requests = new RemoteFilePlaceholderRequest[remoteFiles.Count];
            for (int index = 0; index < remoteFiles.Count; index++)
            {
                RemoteFileSnapshot remote = remoteFiles[index];
                requests[index] = RemoteFilePlaceholderRequestFactory.Create(
                    syncPair,
                    remote.RelativePath,
                    remote.File);
            }

            try
            {
                IReadOnlyList<RemoteFilePlaceholderBatchResult> batchResults =
                    await batchWriter.CreatePlaceholdersAsync(requests, cancellationToken).ConfigureAwait(false);
                if (batchResults.Count != remoteFiles.Count)
                {
                    throw new InvalidOperationException("Batch placeholder writer returned a different number of results.");
                }

                InitialVirtualFilesFileWorkResult[] results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
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
                InitialVirtualFilesFileWorkResult[] results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
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
                        RemoteFilePlaceholderRequestFactory.Create(syncPair, relativePath, remoteFile),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RemoteFilePlaceholderUnavailableException)
            {
                throw;
            }

            return BuildPlaceholderBaseline(syncPair, relativePath, remoteFile, placeholder, existingHydrationState);
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
            SyncFileReconciliationContext context = new SyncFileReconciliationContext(
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
                await BuildLocalMoveCandidateBucketsAsync(
                        localByPath,
                        remoteByPath,
                        stateByPath,
                        options,
                        result,
                        cancellationToken)
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

                MoveCandidateKey candidateKey = new MoveCandidateKey(source.Value.LocalContentHash, source.Value.LocalSizeBytes.Value);
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

            OnlineOnlyPlaceholderMoveContext context = new OnlineOnlyPlaceholderMoveContext(
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

            string[] remoteDeletePlanItems = plan.SourceDirectoryKeys
                .Select(key => RemoteDeletePlanFingerprint.CreateDirectoryItem(
                    key,
                    remoteDirectoriesByPath[key].Node.Id))
                .ToArray();
            SyncDeleteGuard deleteGuard = new(options, plannedLocalDeletes: 0, remoteDeletePlanItems);
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
                    GetParentPath(sourcePath),
                    GetParentPath(targetPath));
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
            SyncRunResult result,
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

                if (result.IsLocalPathDeferred(local.Value.RelativePath))
                {
                    continue;
                }

                try
                {
                    await EnsureLocalContentHashAsync(local.Value, options, cancellationToken).ConfigureAwait(false);
                }
                catch (LocalFileUnavailableException exception)
                {
                    ReportUnavailableLocalFile(result, options, local.Value.RelativePath, exception);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(local.Value.ContentHash))
                {
                    continue;
                }

                MoveCandidateKey candidateKey = new MoveCandidateKey(local.Value.ContentHash, local.Value.SizeBytes);
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
                ReportUnavailableLocalFile(result, options, relativePath, exception);
                return null;
            }
        }

        private static void ReportUnavailableLocalFile(
            SyncRunResult result,
            SyncRunOptions options,
            string relativePath,
            LocalFileUnavailableException exception)
        {
            Report(result, options, SyncActivityKind.Skipped, relativePath, exception.Reason);
            result.RecordDeferredLocalPath(relativePath);
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
            await using VerifyingDownloadStream verifiedDestination = new VerifyingDownloadStream(destination);
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





























































        private static SyncRunOptions CloneWithoutRunProgress(SyncRunOptions options)
        {
            return new SyncRunOptions
            {
                Scope = options.Scope,
                DeleteRemotePermanently = options.DeleteRemotePermanently,
                MinimumLocalUploadAge = options.MinimumLocalUploadAge,
                MaximumLocalDeletesPerRun = options.MaximumLocalDeletesPerRun,
                MaximumRemoteDeletesPerRun = options.MaximumRemoteDeletesPerRun,
                ApprovedRemoteDeletePlan = options.ApprovedRemoteDeletePlan,
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













        private static void Report(
            SyncRunResult result,
            SyncRunOptions options,
            SyncActivityKind kind,
            string relativePath,
            string? details,
            bool requiresUserAction = false,
            bool publishActivityProgress = true)
        {
            SyncActivityReporter.Record(
                result,
                options,
                kind,
                relativePath,
                details,
                requiresUserAction,
                publishActivityProgress);
        }

        private static void ReportTransfer(
            SyncRunOptions options,
            SyncTransferDirection direction,
            string relativePath,
            long transferredBytes,
            long? totalBytes,
            bool isCompleted = false)
        {
            SyncActivityReporter.RecordTransfer(
                options,
                direction,
                relativePath,
                transferredBytes,
                totalBytes,
                isCompleted);
        }

        private async Task EnsureLocalContentHashAsync(
            LocalFileSnapshot local,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            await _contentHashResolver.EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureLocalContentHashForBaselineComparisonAsync(
            LocalFileSnapshot local,
            SyncStateEntry state,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            await _contentHashResolver
                .EnsureForBaselineComparisonAsync(local, state, options, cancellationToken)
                .ConfigureAwait(false);
        }


        private readonly record struct MoveCandidateKey(string ContentHash, long SizeBytes);



        private async Task LogInitialVirtualFilesPopulationHeartbeatAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            Stopwatch stopwatch,
            InitialVirtualFilesPopulationMetrics metrics,
            CancellationToken cancellationToken)
        {
            using PeriodicTimer timer = new PeriodicTimer(InitialVirtualFilesHeartbeatLogInterval);
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




    }
}
