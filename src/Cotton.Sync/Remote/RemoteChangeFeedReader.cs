// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sdk;
using Cotton.Sdk.Sync;
using Cotton.Sync.State;
using System.Net;

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Reads durable remote changes through the SDK and stores per-pair checkpoints.
    /// </summary>
    public class RemoteChangeFeedReader : IRemoteChangeFeedReader
    {
        private readonly ICottonSyncClient _syncClient;
        private readonly ISyncStateStore _stateStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteChangeFeedReader" /> class.
        /// </summary>
        public RemoteChangeFeedReader(ICottonSyncClient syncClient, ISyncStateStore stateStore)
        {
            _syncClient = syncClient ?? throw new ArgumentNullException(nameof(syncClient));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        }

        /// <inheritdoc />
        public async Task<RemoteChangeFeedBatch> ReadAsync(
            string syncPairId,
            int limit = RemoteChangeFeedDefaults.PageSize,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");
            }

            SyncChangeCursor cursor = await _stateStore.GetChangeCursorAsync(syncPairId, cancellationToken).ConfigureAwait(false);
            return await ReadFromCursorAsync(syncPairId, cursor.LastCursor, limit, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<RemoteChangeFeedBatch> ReadFromCursorAsync(
            string syncPairId,
            long sinceCursor,
            int limit = RemoteChangeFeedDefaults.PageSize,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(syncPairId);
            if (sinceCursor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sinceCursor), sinceCursor, "Cursor cannot be negative.");
            }

            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");
            }

            SyncChangesResponseDto response;
            try
            {
                response = await _syncClient
                    .GetChangesAsync(sinceCursor, limit, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CottonApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                throw new RemoteChangeFeedUnavailableException(exception);
            }
            if (response.SinceCursor != sinceCursor)
            {
                throw new InvalidOperationException("Remote change feed response cursor does not match the requested cursor.");
            }

            return new RemoteChangeFeedBatch(
                syncPairId,
                response.SinceCursor,
                response.NextCursor,
                response.HasMore,
                response.CursorExpired,
                response.EarliestAvailableCursor,
                response.Changes);
        }

        /// <inheritdoc />
        public async Task AcknowledgeAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            long lastCursor = batch.CursorExpired ? batch.SinceCursor : batch.NextCursor;
            await SaveCursorAsync(
                batch.SyncPairId,
                lastCursor,
                batch.CursorExpired,
                batch.EarliestAvailableCursor,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task AcknowledgeFullResyncAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            long lastCursor = batch.CursorExpired
                ? batch.EarliestAvailableCursor ?? batch.SinceCursor
                : batch.NextCursor;
            await SaveCursorAsync(
                batch.SyncPairId,
                lastCursor,
                cursorExpired: false,
                batch.EarliestAvailableCursor,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveCursorAsync(
            string syncPairId,
            long lastCursor,
            bool cursorExpired,
            long? earliestAvailableCursor,
            CancellationToken cancellationToken)
        {
            SyncChangeCursor current = await _stateStore
                .GetChangeCursorAsync(syncPairId, cancellationToken)
                .ConfigureAwait(false);
            await _stateStore.SaveChangeCursorAsync(
                new SyncChangeCursor
                {
                    SyncPairId = syncPairId,
                    LastCursor = lastCursor,
                    CursorExpired = cursorExpired,
                    EarliestAvailableCursor = earliestAvailableCursor,
                    HasCompletedFullReconcile = current.HasCompletedFullReconcile,
                    UpdatedAtUtc = DateTime.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
