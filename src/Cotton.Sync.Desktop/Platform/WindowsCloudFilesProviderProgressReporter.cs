// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesProviderProgressReporter(
        IWindowsCloudFilesNativeApi nativeApi,
        WindowsCloudFilesFetchDataRequest request,
        IProgress<SyncTransferProgress>? transferProgress,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null) : IProgress<SyncTransferProgress>
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        private long _lastReportedTimestamp;
        private bool _hasReported;

        public void Report(SyncTransferProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            cancellationToken.ThrowIfCancellationRequested();
            long now = _timeProvider.GetTimestamp();
            if (!_hasReported || value.IsCompleted
                || _timeProvider.GetElapsedTime(_lastReportedTimestamp, now) >= ReportInterval)
            {
                long totalBytes = Math.Max(value.TotalBytes ?? request.FileSizeBytes, value.TransferredBytes);
                nativeApi.ReportProviderProgress(new(
                    request.ConnectionKey, request.TransferKey, totalBytes, value.TransferredBytes));
                _lastReportedTimestamp = now;
                _hasReported = true;
            }

            transferProgress?.Report(value);
        }
    }
}
