// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesProviderProgressReporter(
        IWindowsCloudFilesNativeApi nativeApi,
        WindowsCloudFilesFetchDataRequest request,
        IProgress<SyncTransferProgress>? transferProgress,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        long? totalBytes = null) : IProgress<SyncTransferProgress>
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        private readonly object _gate = new();
        private WindowsCloudFilesProviderProgress _latestProgress = new(
            request.ConnectionKey, request.TransferKey, totalBytes ?? request.FileSizeBytes, 0);
        private long _lastReportedTimestamp;
        private bool _hasReported;

        public async Task RunAsync(Func<CancellationToken, Task> prepareContent)
        {
            ArgumentNullException.ThrowIfNull(prepareContent);
            cancellationToken.ThrowIfCancellationRequested();
            using CancellationTokenSource preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using PeriodicTimer timer = new(ReportInterval, _timeProvider);
            lock (_gate)
            {
                ReportLatestProgress();
            }

            Task heartbeat = ReportPeriodicallyAsync(timer, preparation);
            try
            {
                await prepareContent(preparation.Token).ConfigureAwait(false);
            }
            finally
            {
                timer.Dispose();
                await heartbeat.ConfigureAwait(false);
            }
        }

        public void Report(SyncTransferProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long total = Math.Max(value.TotalBytes ?? request.FileSizeBytes, value.TransferredBytes);
                _latestProgress = new(request.ConnectionKey, request.TransferKey, total, value.TransferredBytes);
                if (!_hasReported || value.IsCompleted
                    || _timeProvider.GetElapsedTime(_lastReportedTimestamp) >= ReportInterval)
                {
                    ReportLatestProgress();
                }
            }

            transferProgress?.Report(value);
        }

        private async Task ReportPeriodicallyAsync(PeriodicTimer timer, CancellationTokenSource preparation)
        {
            try
            {
                while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        if (preparation.IsCancellationRequested)
                        {
                            return;
                        }

                        if (_timeProvider.GetElapsedTime(_lastReportedTimestamp) >= ReportInterval)
                        {
                            ReportLatestProgress();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError("Windows Cloud Files progress failed: {0}", exception);
                await preparation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        private void ReportLatestProgress()
        {
            _lastReportedTimestamp = _timeProvider.GetTimestamp();
            _hasReported = true;
            nativeApi.ReportProviderProgress(_latestProgress);
        }
    }
}
