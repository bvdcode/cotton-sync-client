// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Progress;
using CoreSyncRunProgress = Cotton.Sync.SyncRunProgress;

namespace Cotton.Sync.App.Runners
{
    internal class AppRunProgressReporter : IProgress<CoreSyncRunProgress>
    {
        private readonly IAppRunProgressPublisher _publisher;
        private readonly SyncRunRequest _request;
        private readonly Guid _syncPairId;
        private CoreSyncRunProgress? _latest;
        private bool _isCompleted;

        public AppRunProgressReporter(
            Guid syncPairId,
            IAppRunProgressPublisher publisher,
            SyncRunRequest request)
        {
            _syncPairId = syncPairId;
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public void Report(CoreSyncRunProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _latest = value;
            _isCompleted = value.IsCompleted;
            _publisher.Publish(ToAppRunProgress(_syncPairId, value, _request));
        }

        public void Complete()
        {
            if (_isCompleted)
            {
                return;
            }

            CoreSyncRunProgress? latest = _latest;
            CoreSyncRunProgress completed = new(
                SyncRunProgressStage.Completed,
                latest?.FilesCompleted ?? 0,
                latest?.FilesTotal,
                currentPath: null,
                latest?.StartedAtUtc ?? DateTime.UtcNow,
                isCompleted: true,
                latest?.BytesCompleted ?? 0,
                latest?.BytesTotal);
            Report(completed);
        }

        private static AppRunProgress ToAppRunProgress(
            Guid syncPairId,
            CoreSyncRunProgress progress,
            SyncRunRequest request)
        {
            int requestedPathCount = request.IsFull ? 0 : request.LocalChangedPaths.Count;
            return new AppRunProgress(
                syncPairId,
                progress.Stage,
                progress.FilesCompleted,
                progress.FilesTotal,
                progress.CurrentPath,
                progress.StartedAtUtc,
                progress.IsCompleted,
                progress.OccurredAtUtc,
                progress.BytesCompleted,
                progress.BytesTotal,
                request.Causes,
                request.IsFull,
                requestedPathCount);
        }
    }
}
