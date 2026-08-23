// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.SyncPairs;
using CoreSyncEngine = Cotton.Sync.ISyncEngine;
using CoreSyncPair = Cotton.Sync.SyncPair;
using CoreSyncPairMaterializationMode = Cotton.Sync.SyncPairMaterializationMode;
using CoreSyncRunOptions = Cotton.Sync.SyncRunOptions;
using CoreSyncRunResult = Cotton.Sync.SyncRunResult;
using CoreSyncRunScope = Cotton.Sync.SyncRunScope;

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Runs sync pair work through the headless Cotton sync engine.
    /// </summary>
    public class SyncEnginePairWork : ISyncPairWork
    {
        private static readonly TimeSpan BackgroundMinimumLocalUploadAge = TimeSpan.FromSeconds(2);
        private const int MaximumDeferredLocalRetries = 3;
        private readonly IAppActivityPublisher? _activityPublisher;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly IAppTransferProgressPublisher? _progressPublisher;
        private readonly IAppRunProgressPublisher? _runProgressPublisher;
        private readonly CoreSyncEngine _syncEngine;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncEnginePairWork" /> class.
        /// </summary>
        public SyncEnginePairWork(
            CoreSyncEngine syncEngine,
            IAppActivityPublisher? activityPublisher = null,
            IAppTransferProgressPublisher? progressPublisher = null,
            IAppRunProgressPublisher? runProgressPublisher = null,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        {
            _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
            _activityPublisher = activityPublisher;
            _progressPublisher = progressPublisher;
            _runProgressPublisher = runProgressPublisher;
            _delayAsync = delayAsync ?? Task.Delay;
        }

        /// <inheritdoc />
        public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task RunOnceAsync(SyncPairSettings syncPair, SyncRunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            SyncRunRequest currentRequest = request;
            int deferredRetryCount = 0;
            while (true)
            {
                CoreSyncRunResult result = await RunRequestAsync(syncPair, currentRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (result.RequiresUserAction)
                {
                    throw new SyncActionRequiredException(CreateActionRequiredMessage(result));
                }

                if (!result.HasDeferredLocalPaths)
                {
                    return;
                }

                if (deferredRetryCount >= MaximumDeferredLocalRetries)
                {
                    throw new SyncActionRequiredException(
                        "Local files remained unavailable after "
                        + MaximumDeferredLocalRetries
                        + " retry passes. Close applications using these files and retry sync: "
                        + string.Join(", ", result.DeferredLocalPaths.Take(8)));
                }

                currentRequest = SyncRunRequest.ForLocalChangedPaths(
                    result.DeferredLocalPaths,
                    request.Causes | SyncRunCause.LocalChange);
                deferredRetryCount++;
                await _delayAsync(BackgroundMinimumLocalUploadAge, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<CoreSyncRunResult> RunRequestAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            AppRunProgressReporter? runProgressReporter = _runProgressPublisher is null
                ? null
                : new AppRunProgressReporter(syncPair.Id, _runProgressPublisher, request);
            AppTransferProgressReporter? transferProgressReporter = _progressPublisher is null
                ? null
                : new AppTransferProgressReporter(syncPair.Id, _progressPublisher);
            CoreSyncRunOptions? options = CreateOptionsIfRequired(
                syncPair,
                request,
                runProgressReporter,
                transferProgressReporter);
            try
            {
                return await _syncEngine
                    .RunOnceAsync(ToCorePair(syncPair), options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                transferProgressReporter?.Complete();
                runProgressReporter?.Complete();
            }
        }

        private CoreSyncRunOptions? CreateOptionsIfRequired(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            AppRunProgressReporter? runProgressReporter,
            AppTransferProgressReporter? transferProgressReporter)
        {
            bool allowInitialVirtualFilesStreaming = CanUseInitialVirtualFilesStreaming(request);
            if (CanUseDefaultOptions(request, allowInitialVirtualFilesStreaming))
            {
                return null;
            }

            return CreateOptions(
                syncPair,
                request,
                runProgressReporter,
                transferProgressReporter,
                allowInitialVirtualFilesStreaming);
        }

        private bool CanUseDefaultOptions(
            SyncRunRequest request,
            bool allowInitialVirtualFilesStreaming)
        {
            return _activityPublisher is null
                && _progressPublisher is null
                && _runProgressPublisher is null
                && request.IsFull
                && allowInitialVirtualFilesStreaming
                && (request.Causes & SyncRunCause.InitialPopulation) == SyncRunCause.None
                && request.ApprovedRemoteDeletePlan is null;
        }

        private CoreSyncRunOptions CreateOptions(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            AppRunProgressReporter? runProgressReporter,
            AppTransferProgressReporter? transferProgressReporter,
            bool allowInitialVirtualFilesStreaming)
        {
            return new CoreSyncRunOptions
            {
                Scope = request.IsFull
                    ? CoreSyncRunScope.Full
                    : CoreSyncRunScope.ForLocalChangedPaths(request.LocalChangedPaths, request.LocalDeletedPaths),
                MinimumLocalUploadAge = BackgroundMinimumLocalUploadAge,
                ApprovedRemoteDeletePlan = request.ApprovedRemoteDeletePlan,
                AllowInitialVirtualFilesStreaming = allowInitialVirtualFilesStreaming,
                RestoreMissingRemoteOnlyPlaceholders = request.IsFull
                    && (request.Causes & SyncRunCause.InitialPopulation) != SyncRunCause.None,
                ActivityProgress = _activityPublisher is null ? null : new AppActivityProgressReporter(syncPair.Id, _activityPublisher),
                TransferProgress = transferProgressReporter,
                RunProgress = runProgressReporter,
            };
        }

        private static bool CanUseInitialVirtualFilesStreaming(SyncRunRequest request)
        {
            if (request.LocalChangedPaths.Count != 0)
            {
                return false;
            }

            const SyncRunCause recoveryReconciliationCauses = SyncRunCause.LocalWatcherError
                | SyncRunCause.LocalChangeOverflow
                | SyncRunCause.LocalRenameRecovery
                | SyncRunCause.RemoteCursorExpired;
            if ((request.Causes & recoveryReconciliationCauses) != SyncRunCause.None)
            {
                return false;
            }

            if ((request.Causes & SyncRunCause.InitialPopulation) != SyncRunCause.None)
            {
                return true;
            }

            const SyncRunCause backgroundFullReconciliationCauses = SyncRunCause.RealtimeRemoteChange
                | SyncRunCause.Periodic
                | SyncRunCause.Resume;
            return (request.Causes & backgroundFullReconciliationCauses) == SyncRunCause.None;
        }

        private static CoreSyncPair ToCorePair(SyncPairSettings syncPair)
        {
            return new CoreSyncPair
            {
                SyncPairId = syncPair.Id.ToString("D"),
                LocalRootPath = syncPair.LocalRootPath,
                RemoteRootNodeId = syncPair.RemoteRootNodeId,
                MaterializationMode = ToCoreMaterializationMode(syncPair.Mode),
            };
        }

        private static CoreSyncPairMaterializationMode ToCoreMaterializationMode(SyncPairMode mode)
        {
            return mode == SyncPairMode.WindowsVirtualFiles
                ? CoreSyncPairMaterializationMode.WindowsVirtualFiles
                : CoreSyncPairMaterializationMode.FullMirror;
        }

        private static string CreateActionRequiredMessage(CoreSyncRunResult result)
        {
            return string.IsNullOrWhiteSpace(result.ActionRequiredMessage)
                ? "Sync requires your attention before it can continue."
                : result.ActionRequiredMessage.Trim();
        }
    }
}
