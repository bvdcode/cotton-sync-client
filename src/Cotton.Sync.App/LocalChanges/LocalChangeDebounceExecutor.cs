// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.App.LocalChanges
{
    internal class LocalChangeDebounceExecutor
    {
        private readonly TimeSpan _debounceInterval;
        private readonly ILogger _logger;
        private readonly LocalChangeSyncCoordinator _owner;
        private readonly SyncRequestConnectionRetry _syncRequest;

        public LocalChangeDebounceExecutor(
            LocalChangeSyncCoordinator owner,
            TimeSpan debounceInterval,
            SyncRequestConnectionRetry syncRequest,
            ILogger logger)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _debounceInterval = debounceInterval;
            _syncRequest = syncRequest ?? throw new ArgumentNullException(nameof(syncRequest));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RunAsync(Guid syncPairId, PendingLocalSyncRequest request)
        {
            try
            {
                while (true)
                {
                    string changedPath = await WaitForQuietPathAsync(syncPairId, request).ConfigureAwait(false);
                    if (!_owner.TryCreateSyncDispatch(
                            syncPairId,
                            request,
                            out changedPath,
                            out int dispatchedChangeVersion,
                            out SyncRunRequest? syncRequest))
                    {
                        return;
                    }

                    _logger.LogInformation(
                        "Requesting local-change sync for {SyncPairId} with origin {ChangeOrigin} after change at {ChangedPath}.",
                        syncPairId,
                        "user-or-external",
                        changedPath);
                    if (syncRequest is null)
                    {
                        _logger.LogWarning(
                            "Ignoring local-change sync for {SyncPairId} because no changed path belongs to its local root.",
                            syncPairId);
                        return;
                    }

                    await _syncRequest.RequestAsync(
                            syncPairId,
                            syncRequest,
                            "Local-change sync",
                            request.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (_owner.TryCompleteDispatch(syncPairId, request, dispatchedChangeVersion))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to request local-change sync for {SyncPairId}.", syncPairId);
            }
            finally
            {
                _owner.CompletePendingSync(syncPairId, request);
                request.Cancellation.Dispose();
            }
        }

        private async Task<string> WaitForQuietPathAsync(Guid syncPairId, PendingLocalSyncRequest request)
        {
            while (true)
            {
                int observedChangeVersion = _owner.GetChangeVersion(request);
                TimeSpan remainingMaxDelay = _owner.GetRemainingMaxDebounceDelay(request);
                if (remainingMaxDelay <= TimeSpan.Zero)
                {
                    return _owner.TryGetCurrentChangedPath(syncPairId, request, out string currentPath)
                        ? currentPath
                        : string.Empty;
                }

                TimeSpan delay = remainingMaxDelay < _debounceInterval
                    ? remainingMaxDelay
                    : _debounceInterval;
                await Task.Delay(delay, request.Cancellation.Token).ConfigureAwait(false);
                if (_owner.TryGetQuietChangedPath(
                    syncPairId,
                    request,
                    observedChangeVersion,
                    out string changedPath))
                {
                    return changedPath;
                }
            }
        }
    }
}
