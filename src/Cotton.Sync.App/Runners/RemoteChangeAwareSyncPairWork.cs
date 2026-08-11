// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Wraps sync pair work with durable remote change-feed checkpoint handling.
    /// </summary>
    public class RemoteChangeAwareSyncPairWork : ISyncPairWork
    {
        private readonly ISyncPairWork _inner;
        private readonly IRemoteChangeFeedReader _remoteChanges;
        private readonly RemoteChangeScopedSyncPlanner? _scopedSyncPlanner;
        private readonly ISyncStateStore? _stateStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteChangeAwareSyncPairWork" /> class.
        /// </summary>
        public RemoteChangeAwareSyncPairWork(
            ISyncPairWork inner,
            IRemoteChangeFeedReader remoteChanges,
            ISyncStateStore? stateStore = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _remoteChanges = remoteChanges ?? throw new ArgumentNullException(nameof(remoteChanges));
            _stateStore = stateStore;
            _scopedSyncPlanner = stateStore is null ? null : new RemoteChangeScopedSyncPlanner(stateStore);
        }

        /// <inheritdoc />
        public async Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(syncPair, SyncRunRequest.Full, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task RunOnceAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            RemoteChangeFeedReadResult remoteRead = await ReadRemoteChangesAsync(syncPair, cancellationToken)
                .ConfigureAwait(false);
            RemoteChangeFeedBatch remoteBatch = remoteRead.Batch;
            SyncRunRequest effectiveRequest = await AddPendingFullReconcileCauseAsync(
                    syncPair,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            if (remoteBatch.CursorExpired)
            {
                await RunInnerAsync(
                        syncPair,
                        effectiveRequest.Merge(SyncRunRequest.ForFull(SyncRunCause.RemoteCursorExpired)),
                        cancellationToken)
                    .ConfigureAwait(false);
                await _remoteChanges.AcknowledgeFullResyncAsync(remoteBatch, cancellationToken).ConfigureAwait(false);
                return;
            }

            InnerRequestPlan innerPlan = await ExecuteInnerPlanAsync(
                    syncPair,
                    effectiveRequest,
                    remoteRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (ShouldAcknowledgeRemoteBatch(
                    syncPair,
                    remoteRead,
                    innerPlan.RemoteChangesCovered,
                    innerPlan.Request?.IsFull == true))
            {
                await _remoteChanges.AcknowledgeAsync(remoteBatch, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<InnerRequestPlan> ExecuteInnerPlanAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            RemoteChangeFeedReadResult remoteRead,
            CancellationToken cancellationToken)
        {
            if (CanSkipInnerSync(syncPair, request, remoteRead))
            {
                return new InnerRequestPlan(null, RemoteChangesCovered: true);
            }

            InnerRequestPlan innerPlan = await CreateInnerRequestAsync(
                    syncPair,
                    request,
                    remoteRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (innerPlan.Request is not null)
            {
                await RunInnerAsync(syncPair, innerPlan.Request, cancellationToken).ConfigureAwait(false);
            }

            if (innerPlan.RemoteChangesCovered)
            {
                return innerPlan;
            }

            if (innerPlan.Request is null)
            {
                throw CreateUnresolvedRemotePathException();
            }

            SyncRunRequest? replayRequest = await CreateRemoteReplayRequestAsync(
                    syncPair,
                    request,
                    remoteRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replayRequest is not null)
            {
                await RunInnerAsync(syncPair, replayRequest, cancellationToken).ConfigureAwait(false);
            }

            return new InnerRequestPlan(replayRequest, RemoteChangesCovered: true);
        }

        private async Task<SyncRunRequest> AddPendingFullReconcileCauseAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles
                || !request.IsFull
                || _stateStore is null)
            {
                return request;
            }

            SyncChangeCursor cursor = await _stateStore
                .GetChangeCursorAsync(syncPair.Id.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            return cursor.HasCompletedFullReconcile
                ? request
                : request.Merge(SyncRunRequest.ForFull(SyncRunCause.InitialPopulation));
        }

        private async Task RunInnerAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            await _inner.RunOnceAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles
                || !request.IsFull
                || _stateStore is null)
            {
                return;
            }

            string syncPairId = syncPair.Id.ToString("D");
            SyncChangeCursor cursor = await _stateStore
                .GetChangeCursorAsync(syncPairId, cancellationToken)
                .ConfigureAwait(false);
            if (cursor.HasCompletedFullReconcile)
            {
                return;
            }

            cursor.HasCompletedFullReconcile = true;
            cursor.UpdatedAtUtc = DateTime.UtcNow;
            await _stateStore.SaveChangeCursorAsync(cursor, cancellationToken).ConfigureAwait(false);
        }

        private async Task<RemoteChangeFeedReadResult> ReadRemoteChangesAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            string syncPairId = syncPair.Id.ToString("D");
            RemoteChangeFeedBatch batch = await _remoteChanges
                .ReadAsync(syncPairId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var changes = new List<SyncChangeDto>(batch.Changes);

            while (ShouldReadNextPage(batch))
            {
                batch = await _remoteChanges
                    .ReadFromCursorAsync(syncPairId, batch.NextCursor, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                changes.AddRange(batch.Changes);
            }

            return new RemoteChangeFeedReadResult(batch, RemoteChangeFeedSnapshot.FromChanges(changes));
        }

        private static bool CanSkipInnerSync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            RemoteChangeFeedReadResult remoteRead)
        {
            return syncPair.Mode == SyncPairMode.WindowsVirtualFiles
                && request.IsFull
                && request.LocalChangedPaths.Count == 0
                && CanSkipEmptyFeedFullSync(request.Causes)
                && remoteRead.Batch.SinceCursor > 0
                && !remoteRead.Batch.CursorExpired
                && !remoteRead.Batch.HasMore
                && !remoteRead.HasObservedChanges;
        }

        private static bool CanSkipEmptyFeedFullSync(SyncRunCause causes)
        {
            const SyncRunCause safeCauses = SyncRunCause.Periodic
                | SyncRunCause.RealtimeRemoteChange
                | SyncRunCause.Resume;
            return (causes & ~safeCauses) == SyncRunCause.None;
        }

        private static bool ShouldReadNextPage(RemoteChangeFeedBatch batch)
        {
            if (!batch.HasMore || batch.CursorExpired)
            {
                return false;
            }

            if (batch.NextCursor <= batch.SinceCursor)
            {
                throw new InvalidOperationException("Remote change feed reported more pages without advancing the cursor.");
            }

            return true;
        }

        private async Task<InnerRequestPlan> CreateInnerRequestAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            RemoteChangeFeedReadResult remoteRead,
            CancellationToken cancellationToken)
        {
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles
                || remoteRead.Batch.CursorExpired
                || remoteRead.Batch.HasMore
                || remoteRead.Batch.SinceCursor == 0)
            {
                return new InnerRequestPlan(request, RemoteChangesCovered: true);
            }

            if (!remoteRead.HasObservedChanges)
            {
                SyncRunRequest? localPlan = RemoteChangeScopedSyncPlanner.CreatePlanWithoutMappedRemoteChanges(
                    request,
                    hasUnresolvedChanges: false);
                return new InnerRequestPlan(localPlan, RemoteChangesCovered: true);
            }

            if (RequiresFullReconcileForWindowsVirtualFiles(request))
            {
                return new InnerRequestPlan(request, RemoteChangesCovered: true);
            }

            if (_scopedSyncPlanner is null)
            {
                throw new SyncActionRequiredException(
                    "Remote changes are pending, but scoped VFS planning is unavailable.");
            }

            RemoteChangeScopedSyncPlan scopedPlan = await _scopedSyncPlanner
                .CreatePlanAsync(syncPair, request, remoteRead.Snapshot, cancellationToken)
                .ConfigureAwait(false);
            return new InnerRequestPlan(scopedPlan.Request, !scopedPlan.HasUnresolvedChanges);
        }

        private async Task<SyncRunRequest?> CreateRemoteReplayRequestAsync(
            SyncPairSettings syncPair,
            SyncRunRequest originalRequest,
            RemoteChangeFeedReadResult remoteRead,
            CancellationToken cancellationToken)
        {
            if (_scopedSyncPlanner is null)
            {
                throw new SyncActionRequiredException(
                    "Remote changes are pending, but scoped recovery is unavailable. Refresh the sync folder to rebuild its state.");
            }

            RemoteChangeScopedSyncPlan replayPlan = await _scopedSyncPlanner
                .CreatePlanAsync(
                    syncPair,
                    SyncRunRequest.ForFull(originalRequest.Causes),
                    remoteRead.Snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replayPlan.HasUnresolvedChanges)
            {
                throw CreateUnresolvedRemotePathException();
            }

            return replayPlan.Request;
        }

        private static SyncActionRequiredException CreateUnresolvedRemotePathException()
        {
            return new SyncActionRequiredException(
                "Remote changes inside this sync folder could not be mapped to local paths. "
                + "Refresh the sync folder to rebuild its state.");
        }

        private static bool RequiresFullReconcileForWindowsVirtualFiles(SyncRunRequest request)
        {
            return request.IsFull
                && (request.Causes & SyncRunCause.InitialPopulation) != SyncRunCause.None;
        }

        private static bool ShouldAcknowledgeRemoteBatch(
            SyncPairSettings syncPair,
            RemoteChangeFeedReadResult remoteRead,
            bool remoteChangesCovered,
            bool performedFullSync)
        {
            if (!remoteChangesCovered)
            {
                return false;
            }

            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles
                || remoteRead.HasObservedChanges
                || performedFullSync)
            {
                return true;
            }

            // An empty VFS feed page can be a high-water snapshot before another client's mutation is visible.
            // Keep the cursor pinned unless a full sync actually reconciled the tree.
            return false;
        }

        private record RemoteChangeFeedReadResult(RemoteChangeFeedBatch Batch, RemoteChangeFeedSnapshot Snapshot)
        {
            public bool HasObservedChanges => !Snapshot.IsEmpty;
        }

        private record InnerRequestPlan(SyncRunRequest? Request, bool RemoteChangesCovered);
    }
}
