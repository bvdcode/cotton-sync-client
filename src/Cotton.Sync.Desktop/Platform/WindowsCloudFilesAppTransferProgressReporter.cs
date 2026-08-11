// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync;
using Cotton.Sync.App.Progress;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesAppTransferProgressReporter : IProgress<SyncTransferProgress>
    {
        private readonly AppTransferProgressEstimator _estimator = new();
        private readonly IAppTransferProgressPublisher _publisher;
        private readonly Guid _syncPairId;
        private SyncTransferProgress? _latest;

        public WindowsCloudFilesAppTransferProgressReporter(
            Guid syncPairId,
            IAppTransferProgressPublisher publisher)
        {
            _syncPairId = syncPairId;
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        }

        public void Report(SyncTransferProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _latest = value;
            AppTransferProgressEstimate estimate = _estimator.AddSample(
                value.Direction,
                value.RelativePath,
                value.TransferredBytes,
                value.TotalBytes,
                value.IsCompleted,
                value.OccurredAtUtc);
            _publisher.Publish(
                new AppTransferProgress(
                    _syncPairId,
                    value.Direction,
                    value.RelativePath,
                    value.TransferredBytes,
                    value.TotalBytes,
                    value.IsCompleted,
                    value.OccurredAtUtc,
                    estimate.SpeedBytesPerSecond,
                    estimate.EstimatedTimeRemaining));
        }

        public void Complete()
        {
            SyncTransferProgress? latest = _latest;
            if (latest is null || latest.IsCompleted)
            {
                return;
            }

            Report(new SyncTransferProgress(
                latest.Direction,
                latest.RelativePath,
                latest.TransferredBytes,
                latest.TotalBytes,
                isCompleted: true));
        }
    }
}
