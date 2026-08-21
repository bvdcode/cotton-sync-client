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
        private readonly InitialVirtualFilesPopulationPipeline _initialVirtualFilesPopulationPipeline;
        private readonly InitialVirtualFilesPopulationCoordinator _initialVirtualFilesPopulationCoordinator;
        private readonly SyncFilePhaseRunner _filePhaseRunner;
        private readonly SyncStateFileHashLoader _stateFileHashLoader;
        private readonly SyncRunCoordinator _runCoordinator;

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
            _initialVirtualFilesPopulationPipeline = new InitialVirtualFilesPopulationPipeline(
                _remoteStreamingCrawler,
                _initialVirtualFilesFileBatchProcessor,
                _remoteDirectoryTreePopulationObserver,
                _directoryReconciler,
                _fileDeleteExecutor);
            _initialVirtualFilesPopulationCoordinator = new InitialVirtualFilesPopulationCoordinator(
                _initialVirtualFilesStreamingPlanner,
                _remoteFilePlaceholderPopulationObserver,
                _initialVirtualFilesPopulationPipeline,
                _initialVirtualFilesHeartbeatLogger,
                _logger);
            _filePhaseRunner = new SyncFilePhaseRunner(_fileReconciler);
            _stateFileHashLoader = new SyncStateFileHashLoader(_contentHashResolver);
            _runCoordinator = new SyncRunCoordinator(
                _treeScanner,
                _stateSnapshotLoader,
                _scopedDirectoryRenamePlanner,
                _remoteDirectoryMoveCoordinator,
                _directoryReconciler,
                _stateFileHashLoader,
                _onlineOnlyPlaceholderMoveCoordinator,
                _localFileMoveCoordinator,
                _scopedDirectoryDeleteExecutor,
                _directoryDeleteReconciler,
                _logger);
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
            SyncRunResult? initialVirtualFilesResult = await _initialVirtualFilesPopulationCoordinator.TryRunAsync(
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

            SyncRunContext context = await _runCoordinator.PrepareAsync(
                    syncPair,
                    runOptions,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> directoryPathKeys = await _runCoordinator.ReconcileDirectoriesAsync(context)
                .ConfigureAwait(false);
            SyncDeletePlan deletePlan = await _runCoordinator.BuildDeletePlanAsync(context).ConfigureAwait(false);
            await _runCoordinator.ReconcilePlannedDirectoryDeletesAsync(context, deletePlan, directoryPathKeys)
                .ConfigureAwait(false);
            SyncFilePhaseResult filePhase = await _filePhaseRunner.RunAsync(context, deletePlan).ConfigureAwait(false);
            await _runCoordinator.CompleteAsync(context, deletePlan, directoryPathKeys, filePhase)
                .ConfigureAwait(false);
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











































































































































































































































































    }
}
