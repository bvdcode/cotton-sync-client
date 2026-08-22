// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Models.Enums;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Tests.Runners
{
    public partial class RemoteChangeAwareSyncPairWorkTests
    {
        private class FakeRemoteChangeFeedReader : IRemoteChangeFeedReader
        {
            private readonly Queue<RemoteChangeFeedBatch> _batches;

            public FakeRemoteChangeFeedReader(params RemoteChangeFeedBatch[] batches)
            {
                _batches = new Queue<RemoteChangeFeedBatch>(batches);
            }

            public List<string> ReadSyncPairIds { get; } = [];

            public List<(string SyncPairId, long SinceCursor)> ReadFromCursorRequests { get; } = [];

            public List<int> ReadLimits { get; } = [];

            public List<RemoteChangeFeedBatch> AcknowledgedBatches { get; } = [];

            public List<RemoteChangeFeedBatch> FullResyncAcknowledgedBatches { get; } = [];

            public Queue<Exception> ReadFailures { get; } = [];

            public Task<RemoteChangeFeedBatch> ReadAsync(
                string syncPairId,
                int limit = RemoteChangeFeedDefaults.PageSize,
                CancellationToken cancellationToken = default)
            {
                ReadSyncPairIds.Add(syncPairId);
                ReadLimits.Add(limit);
                if (ReadFailures.TryDequeue(out Exception? failure))
                {
                    throw failure;
                }

                return Task.FromResult(_batches.Dequeue());
            }

            public Task<RemoteChangeFeedBatch> ReadFromCursorAsync(
                string syncPairId,
                long sinceCursor,
                int limit = RemoteChangeFeedDefaults.PageSize,
                CancellationToken cancellationToken = default)
            {
                ReadFromCursorRequests.Add((syncPairId, sinceCursor));
                ReadLimits.Add(limit);
                return Task.FromResult(_batches.Dequeue());
            }

            public Task AcknowledgeAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
            {
                AcknowledgedBatches.Add(batch);
                return Task.CompletedTask;
            }

            public Task AcknowledgeFullResyncAsync(RemoteChangeFeedBatch batch, CancellationToken cancellationToken = default)
            {
                FullResyncAcknowledgedBatches.Add(batch);
                return Task.CompletedTask;
            }
        }

        private static IReadOnlyCollection<SyncChangeDto> CreateFileChanges(
            long firstCursor,
            int count,
            Guid parentNodeId)
        {
            return Enumerable
                .Range(0, count)
                .Select(index => new SyncChangeDto
                {
                    Id = firstCursor + index,
                    Kind = SyncChangeKind.FileCreated,
                    LayoutId = Guid.NewGuid(),
                    ItemId = Guid.NewGuid(),
                    ParentNodeId = parentNodeId,
                    Name = $"file-{firstCursor + index}.txt",
                })
                .ToArray();
        }
    }
}
