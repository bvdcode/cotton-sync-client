// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Models.Enums;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        private class FinalizationChangeFeed(SyncPairSettings pair) : IRemoteChangeFeedReader
        {
            public long Cursor { get; private set; }

            public Task<RemoteChangeFeedBatch> ReadAsync(
                string syncPairId, int limit = RemoteChangeFeedDefaults.PageSize, CancellationToken cancellationToken = default)
            {
                return ReadFromCursorAsync(syncPairId, Cursor, limit, cancellationToken);
            }

            public Task<RemoteChangeFeedBatch> ReadFromCursorAsync(
                string syncPairId, long sinceCursor, int limit = RemoteChangeFeedDefaults.PageSize, CancellationToken cancellationToken = default)
            {
                const int totalChanges = 1001;
                int count = Math.Min(limit, totalChanges - checked((int)sinceCursor));
                SyncChangeDto[] changes = Enumerable.Range(1, count).Select(index => new SyncChangeDto
                {
                    Id = sinceCursor + index,
                    Kind = SyncChangeKind.FileCreated,
                    LayoutId = Guid.NewGuid(),
                    ItemId = Guid.NewGuid(),
                    ParentNodeId = pair.RemoteRootNodeId,
                    Name = "new-remote-" + (sinceCursor + index) + ".txt",
                }).ToArray();
                long next = sinceCursor + count;
                return Task.FromResult(new RemoteChangeFeedBatch(
                    pair.Id.ToString("D"), sinceCursor, next, next < totalChanges, false, null, changes));
            }

            public Task AcknowledgeAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
            {
                Cursor = batch.NextCursor;
                return Task.CompletedTask;
            }

            public Task AcknowledgeFullResyncAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
            {
                return AcknowledgeAsync(batch, cancellationToken);
            }
        }
    }
}
