// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Remote;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesHeartbeatLogger(ILogger logger)
    {
        private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

        public async Task RunAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            Stopwatch stopwatch,
            InitialVirtualFilesPopulationMetrics metrics,
            CancellationToken cancellationToken)
        {
            using PeriodicTimer timer = new(LogInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                LogHeartbeat(syncPair, options, stopwatch, metrics);
            }
        }

        public static async Task IgnoreExpectedCancellationAsync(
            Task heartbeat,
            CancellationToken cancellationToken)
        {
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void LogHeartbeat(
            SyncPair syncPair,
            SyncRunOptions options,
            Stopwatch stopwatch,
            InitialVirtualFilesPopulationMetrics metrics)
        {
            int createdPlaceholders = metrics.CreatedPlaceholders;
            double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d);
            int discoveredDirectoryCount = metrics.DiscoveredDirectories;
            int discoveredFileCount = metrics.DiscoveredFiles;
            int stateFileRowsWritten = metrics.StateFileRowsWritten;
            int stateDirectoryRowsWritten = metrics.StateDirectoryRowsWritten;
            double discoveredDirectoryRatePerSecond = discoveredDirectoryCount / elapsedSeconds;
            double discoveredFileRatePerSecond = discoveredFileCount / elapsedSeconds;
            double createdPlaceholderRatePerSecond = createdPlaceholders / elapsedSeconds;
            double stateWriteRatePerSecond = (stateFileRowsWritten + stateDirectoryRowsWritten) / elapsedSeconds;
            RemoteTreeScanProgressCounter remoteScanProgress = metrics.RemoteScanProgress;
            int remotePageCount = remoteScanProgress.PagesScanned;
            double remotePageAverageLatencyMilliseconds = remotePageCount <= 0
                ? 0d
                : remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds / remotePageCount;
            long managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            metrics.RecordManagedHeapSample(managedHeapBytes);
            logger.LogInformation(
                "Initial streaming Windows virtual-files population heartbeat for pair {SyncPairId}: elapsed={ElapsedMilliseconds} ms; discovered directories={DirectoryCount} at {DirectoryDiscoveryRatePerSecond:F2} dirs/sec, files={FileCount} at {FileDiscoveryRatePerSecond:F2} files/sec; completed directories={CompletedDirectoryCount}, files={CompletedFileCount}; remote pages read={RemotePageCount}, remote page latency total={RemotePageLatencyTotalMilliseconds:F0} ms, avg={RemotePageLatencyAverageMilliseconds:F2} ms, max={RemotePageLatencyMaxMilliseconds:F0} ms, last={RemotePageLatencyLastMilliseconds:F0} ms; placeholders created or refreshed={CreatedPlaceholderCount}, current skipped={SkippedCurrentPlaceholderCount}, user-action skipped={SkippedUnavailablePlaceholderCount}, rate={CreatedPlaceholderRatePerSecond:F2} placeholders/sec; state writes file rows={StateFileRowsWritten}, file batches={StateFileWriteBatchCount}, directory rows={StateDirectoryRowsWritten}, state write rate={StateWriteRatePerSecond:F2} rows/sec; managed heap={ManagedHeapBytes} bytes; queue capacity={QueueCapacity}, placeholder concurrency={PlaceholderConcurrency}, placeholder batch size={PlaceholderBatchSize}, state batch size={StateBatchSize}.",
                syncPair.SyncPairId,
                stopwatch.ElapsedMilliseconds,
                discoveredDirectoryCount,
                discoveredDirectoryRatePerSecond,
                discoveredFileCount,
                discoveredFileRatePerSecond,
                metrics.CompletedDirectories,
                metrics.CompletedFiles,
                remotePageCount,
                remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds,
                remotePageAverageLatencyMilliseconds,
                remoteScanProgress.PageReadLatencyMax.TotalMilliseconds,
                remoteScanProgress.LastPageReadLatency.TotalMilliseconds,
                createdPlaceholders,
                metrics.SkippedCurrentPlaceholders,
                metrics.SkippedUnavailablePlaceholders,
                createdPlaceholderRatePerSecond,
                stateFileRowsWritten,
                metrics.StateFileWriteBatches,
                stateDirectoryRowsWritten,
                stateWriteRatePerSecond,
                managedHeapBytes,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize);
        }
    }
}
