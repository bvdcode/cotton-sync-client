// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.LocalChanges
{
    internal class OfflineLocalChangeReconciler
    {
        private readonly ILocalOfflineChangeDetector _detector;
        private readonly ILogger _logger;
        private readonly SyncRequestConnectionRetry _syncRequest;

        public OfflineLocalChangeReconciler(
            ILocalOfflineChangeDetector detector,
            SyncRequestConnectionRetry syncRequest,
            ILogger logger)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _syncRequest = syncRequest ?? throw new ArgumentNullException(nameof(syncRequest));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ReconcileAsync(SyncPairSettings syncPair, CancellationToken cancellationToken)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return;
            }

            SyncRunRequest? request = await DetectAsync(syncPair, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            _logger.LogInformation(
                "Requesting startup local-change reconciliation for {SyncPairId}; full={IsFull}; requested paths={RequestedPathCount}; paths={ChangedPathPreview}.",
                syncPair.Id,
                request.IsFull,
                request.LocalChangedPaths.Count,
                string.Join(", ", request.LocalChangedPaths.Take(8)));
            try
            {
                await _syncRequest.RequestAsync(
                        syncPair.Id,
                        request,
                        "Startup local-change reconciliation",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Startup local-change reconciliation failed for {SyncPairId}.",
                    syncPair.Id);
            }
        }

        private async Task<SyncRunRequest?> DetectAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _detector.DetectAsync(syncPair, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Failed to inspect local changes made while sync was stopped for {SyncPairId}; requesting a full recovery pass.",
                    syncPair.Id);
                return SyncRunRequest.ForFull(SyncRunCause.LocalWatcherError);
            }
        }
    }
}
