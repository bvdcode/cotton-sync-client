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
using static Cotton.Sync.LocalUploadPolicy;
using static Cotton.Sync.RemoteSyncErrorClassifier;
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
        private readonly SyncTreeScanner _treeScanner;
        private readonly SyncStateSnapshotLoader _stateSnapshotLoader;
        private readonly ScopedVirtualFilesDirectoryRenamePlanner _scopedDirectoryRenamePlanner;
        private readonly InitialVirtualFilesStreamingPlanner _initialVirtualFilesStreamingPlanner;
        private readonly InitialVirtualFilesHeartbeatLogger _initialVirtualFilesHeartbeatLogger;
        private readonly SyncRemoteFileTransfer _fileTransfer;
        private readonly SyncFileConflictResolver _conflictResolver;
        private readonly SyncFileUploadExecutor _fileUploadExecutor;
        private readonly SyncFileMaterializer _fileMaterializer;
        private readonly SyncFileDeleteExecutor _fileDeleteExecutor;
        private readonly SyncPlaceholderReconciler _placeholderReconciler;
        private readonly SyncFileReconciler _fileReconciler;
        private readonly SyncLocalFileMoveCoordinator _localFileMoveCoordinator;
        private readonly ScopedVirtualFilesDirectoryDeleteExecutor _scopedDirectoryDeleteExecutor;
        private readonly SyncOnlineOnlyPlaceholderMoveCoordinator _onlineOnlyPlaceholderMoveCoordinator;
        private readonly InitialVirtualFilesFileBatchProcessor _initialVirtualFilesFileBatchProcessor;

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
            _treeScanner = new SyncTreeScanner(
                _localScanner,
                _localContentHasher,
                _localMetadataTreeScanner,
                _localMetadataTreeLookupScanner,
                _localMetadataPathLookupScanner,
                _localTreeScanner,
                _remoteCrawler,
                _remoteLookupCrawler,
                _remotePathLookupCrawler,
                _logger);
            _stateSnapshotLoader = new SyncStateSnapshotLoader(_stateStore);
            _scopedDirectoryRenamePlanner = new ScopedVirtualFilesDirectoryRenamePlanner(
                _localMetadataPathLookupScanner,
                _remotePathLookupCrawler,
                _stateStore);
            _initialVirtualFilesStreamingPlanner = new InitialVirtualFilesStreamingPlanner(
                _remoteStreamingCrawler,
                _remoteFilePlaceholderWriter,
                _stateStore,
                _localMetadataPathLookupScanner,
                _treeScanner,
                _logger);
            _initialVirtualFilesHeartbeatLogger = new InitialVirtualFilesHeartbeatLogger(_logger);
            _fileTransfer = new SyncRemoteFileTransfer(
                _localWriter,
                _remoteFiles,
                _remoteFileMaterializationObserver,
                _remotePathLookupCrawler);
            _conflictResolver = new SyncFileConflictResolver(
                _contentHashResolver,
                _localWriter,
                _fileTransfer,
                _stateStore);
            _fileUploadExecutor = new SyncFileUploadExecutor(
                _contentHashResolver,
                _fileTransfer,
                _conflictResolver,
                _stateStore,
                _logger);
            _fileMaterializer = new SyncFileMaterializer(
                _remoteFilePlaceholderWriter,
                _stateStore,
                _localWriter,
                _fileTransfer);
            _fileDeleteExecutor = new SyncFileDeleteExecutor(
                _remoteFiles,
                _localWriter,
                _stateStore,
                _fileTransfer,
                _conflictResolver);
            _placeholderReconciler = new SyncPlaceholderReconciler(
                _fileMaterializer,
                _conflictResolver,
                _fileDeleteExecutor,
                _fileUploadExecutor,
                _stateStore);
            _fileReconciler = new SyncFileReconciler(
                _fileMaterializer,
                _fileUploadExecutor,
                _conflictResolver,
                _placeholderReconciler,
                _fileDeleteExecutor,
                _stateStore,
                _contentHashResolver);
            _localFileMoveCoordinator = new SyncLocalFileMoveCoordinator(
                _contentHashResolver,
                _remoteFiles,
                _fileTransfer,
                _stateStore);
            _scopedDirectoryDeleteExecutor = new ScopedVirtualFilesDirectoryDeleteExecutor(
                _remoteDirectories,
                _stateStore);
            _onlineOnlyPlaceholderMoveCoordinator = new SyncOnlineOnlyPlaceholderMoveCoordinator(
                _fileMaterializer,
                _stateStore,
                _remoteFiles,
                _fileTransfer);
            _initialVirtualFilesFileBatchProcessor = new InitialVirtualFilesFileBatchProcessor(
                _remoteFilePlaceholderWriter,
                _fileMaterializer,
                _stateStore,
                _localFilePresenceProbe);
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
                _initialVirtualFilesStreamingPlanner.CanRun(syncPair, runOptions);
            if (!initialWindowsVirtualFilesStreamingCanApply)
            {
                SyncRunProgressReporter.ReportRunProgress(runOptions, SyncRunProgressStage.ScanningLocal, 0, null, null, startedAtUtc);
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
                SyncRunProgressReporter.ReportRunProgress(runOptions, SyncRunProgressStage.ScanningLocal, 0, null, null, startedAtUtc);
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
            SyncTreeLookups treeLookups = await _treeScanner.ScanAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            (Dictionary<string, SyncStateEntry> directoryStateByPath, Dictionary<string, SyncStateEntry> fileStateByPath) =
                await _stateSnapshotLoader.LoadAsync(
                        syncPair.SyncPairId,
                        options,
                        treeLookups,
                        cancellationToken)
                    .ConfigureAwait(false);
            ScopedVirtualFilesDirectoryRenamePlan? scopedDirectoryRename =
                await _scopedDirectoryRenamePlanner.ExpandAsync(
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
            SyncRunProgressReporter.ReportRunProgress(
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
            await _onlineOnlyPlaceholderMoveCoordinator.CoalesceAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await _localFileMoveCoordinator.CoalesceAsync(
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
                await _scopedDirectoryDeleteExecutor.DeleteConfirmedScopedVirtualFilesDirectoryRenameSourceAsync(
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
                ScopedVirtualFilesDirectoryDeletePlanner.Build(
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
                        await _fileReconciler.ReconcileWithoutBaselineAsync(
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
                        await _fileReconciler.ReconcileWithBaselineAsync(
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
                    ReportUnavailable(context.Result, context.Options, relativePath, exception);
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
                await _scopedDirectoryDeleteExecutor.DeleteConfirmedScopedVirtualFilesDirectorySubtreesAsync(
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

            SyncRunProgressReporter.ReportRunProgress(
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
                    if (!ShouldDefer(local, options, out _))
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
                            ReportUnavailable(result, options, local.RelativePath, exception);
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





        private async Task<SyncRunResult?> TryRunInitialWindowsVirtualFilesStreamingPopulationAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            InitialVirtualFilesStreamingPlanDecision streamingPlanDecision =
                await _initialVirtualFilesStreamingPlanner.CreateDecisionAsync(
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
                SyncRunProgressReporter.ReportRunProgress(options, SyncRunProgressStage.CreatingPlaceholders, 0, null, null, startedAtUtc);
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
            Task heartbeat = _initialVirtualFilesHeartbeatLogger.RunAsync(
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
                await InitialVirtualFilesHeartbeatLogger.IgnoreExpectedCancellationAsync(heartbeat, heartbeatCancellation.Token).ConfigureAwait(false);
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
            int completedItems = InitialVirtualFilesProgress.GetItemCount(metrics.CompletedFiles, metrics.CompletedDirectories);
            int discoveredItems = InitialVirtualFilesProgress.GetItemCount(metrics.DiscoveredFiles, metrics.DiscoveredDirectories);
            int totalItems = Math.Max(completedItems, discoveredItems);
            if (!streamingPlan.SkipCurrentPlaceholders || metrics.LastPlaceholderProgressReportedAtUtc.HasValue)
            {
                SyncRunProgressReporter.ReportRunProgress(
                    options,
                    SyncRunProgressStage.CreatingPlaceholders,
                    completedItems,
                    totalItems,
                    null,
                    startedAtUtc);
            }
            SyncRunProgressReporter.ReportRunProgress(
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
            int placeholderBatchSize = _initialVirtualFilesFileBatchProcessor.SupportsBatchWriting
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
            _initialVirtualFilesFileBatchProcessor.EnqueueInitialVirtualFilesFileBatchWork(
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
                int flushedDirectoryRows = await _initialVirtualFilesFileBatchProcessor.FlushInitialVirtualFilesStateBatchAsync(
                        state.PendingDirectoryStates,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                context.Metrics.RecordDirectoryStateWrite(flushedDirectoryRows);
            }

            context.Metrics.RecordCompletedDirectory();
            InitialVirtualFilesProgress.Report(context, directory.RelativePath);
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
                _initialVirtualFilesFileBatchProcessor.TryCreateCurrentInitialVirtualFilesFileWorkResult(context.SyncPair, file, context.StreamingPlan);
            if (currentPlaceholderWorkResult is not null)
            {
                await _initialVirtualFilesFileBatchProcessor.CompleteInitialVirtualFilesFileWorkAsync(
                        currentPlaceholderWorkResult,
                        state.PendingFileStates,
                        context)
                    .ConfigureAwait(false);
                return;
            }

            state.PendingFileBatch.Add(file);
            int placeholderBatchSize = _initialVirtualFilesFileBatchProcessor.SupportsBatchWriting
                ? context.Options.InitialVirtualFilesPlaceholderBatchSize
                : 1;
            if (state.PendingFileBatch.Count >= placeholderBatchSize)
            {
                _initialVirtualFilesFileBatchProcessor.EnqueueInitialVirtualFilesFileBatchWork(
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
            _initialVirtualFilesFileBatchProcessor.EnqueueInitialVirtualFilesFileBatchWork(
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
            SyncRunProgressReporter.ReportRunProgress(
                context.Options,
                SyncRunProgressStage.FinalizingCloudFiles,
                0,
                directoriesDiscovered,
                null,
                context.StartedAtUtc);
            await _remoteDirectoryTreePopulationObserver
                .AfterDirectoryTreePopulationAsync(requests.Values.ToArray(), context.CancellationToken)
                .ConfigureAwait(false);
            SyncRunProgressReporter.ReportRunProgress(
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
            int flushedFileRows = await _initialVirtualFilesFileBatchProcessor.FlushInitialVirtualFilesStateBatchAsync(
                    state.PendingFileStates,
                    context.CancellationToken)
                .ConfigureAwait(false);
            context.Metrics.RecordFileStateWrite(flushedFileRows);
            int flushedDirectoryRows = await _initialVirtualFilesFileBatchProcessor.FlushInitialVirtualFilesStateBatchAsync(
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
                await _fileDeleteExecutor.DeleteLocalAsync(
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
                await _initialVirtualFilesFileBatchProcessor.CompleteInitialVirtualFilesFileWorkBatchAsync(
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
                await _initialVirtualFilesFileBatchProcessor.CompleteInitialVirtualFilesFileWorkBatchAsync(
                        await task.ConfigureAwait(false),
                        pendingFileStates,
                        context)
                    .ConfigureAwait(false);
            }
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











    }
}
