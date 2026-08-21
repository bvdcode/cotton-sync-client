// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Threading.Channels;
using Cotton.Sync.Remote;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesPopulationCoordinator(
        InitialVirtualFilesStreamingPlanner planner,
        IRemoteFilePlaceholderPopulationObserver? populationObserver,
        InitialVirtualFilesPopulationPipeline pipeline,
        InitialVirtualFilesHeartbeatLogger heartbeatLogger,
        ILogger logger)
    {
        public async Task<SyncRunResult?> TryRunAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            InitialVirtualFilesStreamingPlanDecision streamingPlanDecision =
                await planner.CreateDecisionAsync(
                        syncPair,
                        options,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            InitialVirtualFilesStreamingPlan? streamingPlan = streamingPlanDecision.Plan;
            if (streamingPlan is null)
            {
                return null;
            }

            long startingManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            LogInitialVirtualFilesPopulationStarted(syncPair, options, startingManagedHeapBytes);
            Stopwatch stopwatch = Stopwatch.StartNew();
            SyncRunResult result = new();
            Channel<InitialVirtualFilesPopulationItem> channel = Channel.CreateBounded<InitialVirtualFilesPopulationItem>(
                new BoundedChannelOptions(options.InitialVirtualFilesPopulationQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
            InitialVirtualFilesPopulationMetrics metrics = new(startingManagedHeapBytes);
            InitialVirtualFilesRemoteProgressReporter initialVirtualFilesProgress = new(
                metrics.RemoteScanProgress,
                options,
                startedAtUtc,
                publishRunProgress: !streamingPlan.SkipCurrentPlaceholders,
                metrics);
            if (!streamingPlan.SkipCurrentPlaceholders)
            {
                SyncRunProgressReporter.ReportRunProgress(options, SyncRunProgressStage.CreatingPlaceholders, 0, null, null, startedAtUtc);
            }

            using IDisposable? providerWriteBurst = populationObserver
                ?.BeginPopulation(syncPair.SyncPairId, syncPair.LocalRootPath);
            using CancellationTokenSource streamingCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            InitialVirtualFilesPopulationSink sink = new(channel.Writer, metrics);
            InitialVirtualFilesPopulationContext context = new(
                syncPair,
                options,
                result,
                channel.Reader,
                startedAtUtc,
                streamingPlan,
                metrics,
                streamingCancellation.Token);
            Task producer = pipeline.ProduceAsync(
                syncPair,
                options,
                startedAtUtc,
                channel,
                sink,
                initialVirtualFilesProgress,
                streamingCancellation.Token);
            Task consumer = pipeline.ConsumeAsync(context);
            using CancellationTokenSource heartbeatCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task heartbeat = heartbeatLogger.RunAsync(
                syncPair,
                options,
                stopwatch,
                metrics,
                heartbeatCancellation.Token);
            await InitialVirtualFilesPopulationPipeline.RunAsync(
                    producer,
                    consumer,
                    heartbeat,
                    channel.Writer,
                    streamingCancellation,
                    heartbeatCancellation)
                .ConfigureAwait(false);

            stopwatch.Stop();
            CompleteInitialVirtualFilesPopulation(syncPair, options, result, startedAtUtc, streamingPlan, metrics, stopwatch);
            return result;
        }

        private void LogInitialVirtualFilesPopulationStarted(
            SyncPair syncPair,
            SyncRunOptions options,
            long startingManagedHeapBytes)
        {
            logger.LogInformation(
                "Starting initial streaming Windows virtual-files population for pair {SyncPairId} with queue capacity {QueueCapacity}, placeholder concurrency {PlaceholderConcurrency}, placeholder batch size {PlaceholderBatchSize}, state batch size {StateBatchSize}, and managed heap {ManagedHeapBytes} bytes.",
                syncPair.SyncPairId,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize,
                startingManagedHeapBytes);
        }

        private void CompleteInitialVirtualFilesPopulation(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            DateTime startedAtUtc,
            InitialVirtualFilesStreamingPlan streamingPlan,
            InitialVirtualFilesPopulationMetrics metrics,
            Stopwatch stopwatch)
        {
            int completedItems = InitialVirtualFilesProgress.GetItemCount(metrics.CompletedFiles, metrics.CompletedDirectories);
            int discoveredItems = InitialVirtualFilesProgress.GetItemCount(metrics.DiscoveredFiles, metrics.DiscoveredDirectories);
            int totalItems = Math.Max(completedItems, discoveredItems);
            if (!streamingPlan.SkipCurrentPlaceholders || metrics.LastPlaceholderProgressReportedAtUtc.HasValue)
            {
                SyncRunProgressReporter.ReportRunProgress(
                    options,
                    SyncRunProgressStage.CreatingPlaceholders,
                    completedItems,
                    totalItems,
                    null,
                    startedAtUtc);
            }
            SyncRunProgressReporter.ReportRunProgress(
                options,
                SyncRunProgressStage.Completed,
                completedItems,
                totalItems,
                null,
                startedAtUtc,
                isCompleted: true);
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds <= 0d
                ? 1d
                : stopwatch.Elapsed.TotalSeconds;
            int finalDiscoveredDirectoryCount = metrics.DiscoveredDirectories;
            int finalDiscoveredFileCount = metrics.DiscoveredFiles;
            double discoveredDirectoryRatePerSecond = finalDiscoveredDirectoryCount / elapsedSeconds;
            double discoveredFileRatePerSecond = finalDiscoveredFileCount / elapsedSeconds;
            double createdPlaceholderRatePerSecond = metrics.CreatedPlaceholders / elapsedSeconds;
            double stateWriteRatePerSecond =
                (metrics.StateFileRowsWritten + metrics.StateDirectoryRowsWritten) / elapsedSeconds;
            RemoteTreeScanProgressCounter remoteScanProgress = metrics.RemoteScanProgress;
            int remotePageCount = remoteScanProgress.PagesScanned;
            double remotePageAverageLatencyMilliseconds = remotePageCount <= 0
                ? 0d
                : remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds / remotePageCount;
            long completedManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            metrics.RecordManagedHeapSample(completedManagedHeapBytes);
            logger.LogInformation(
                "Completed initial streaming Windows virtual-files population for pair {SyncPairId} with {DirectoryCount} directories discovered at {DirectoryDiscoveryRatePerSecond:F2} dirs/sec, {FileCount} files discovered at {FileDiscoveryRatePerSecond:F2} files/sec, remote pages read={RemotePageCount}, remote page latency total={RemotePageLatencyTotalMilliseconds:F0} ms, avg={RemotePageLatencyAverageMilliseconds:F2} ms, max={RemotePageLatencyMaxMilliseconds:F0} ms, last={RemotePageLatencyLastMilliseconds:F0} ms, {CompletedFileCount} file items completed, {CreatedPlaceholderCount} placeholders created or refreshed, {SkippedCurrentPlaceholderCount} current placeholders skipped, {SkippedUnavailablePlaceholderCount} placeholders skipped with user action in {ElapsedMilliseconds} ms at {CreatedPlaceholderRatePerSecond:F2} placeholders/sec; state writes {StateFileRowsWritten} file rows, file write batches {StateFileWriteBatchCount}, directory rows {StateDirectoryRowsWritten}, state write rate={StateWriteRatePerSecond:F2} rows/sec; managed heap start={StartingManagedHeapBytes} bytes, completed={CompletedManagedHeapBytes} bytes, peak={PeakManagedHeapBytes} bytes, delta={ManagedHeapDeltaBytes} bytes; queue capacity={QueueCapacity}, placeholder concurrency={PlaceholderConcurrency}, placeholder batch size={PlaceholderBatchSize}, state batch size={StateBatchSize}; activities retained {RetainedActivityCount}/{TotalActivityCount}, truncated={ActivityListTruncated}.",
                syncPair.SyncPairId,
                finalDiscoveredDirectoryCount,
                discoveredDirectoryRatePerSecond,
                finalDiscoveredFileCount,
                discoveredFileRatePerSecond,
                remotePageCount,
                remoteScanProgress.PageReadLatencyTotal.TotalMilliseconds,
                remotePageAverageLatencyMilliseconds,
                remoteScanProgress.PageReadLatencyMax.TotalMilliseconds,
                remoteScanProgress.LastPageReadLatency.TotalMilliseconds,
                metrics.CompletedFiles,
                metrics.CreatedPlaceholders,
                metrics.SkippedCurrentPlaceholders,
                metrics.SkippedUnavailablePlaceholders,
                stopwatch.ElapsedMilliseconds,
                createdPlaceholderRatePerSecond,
                metrics.StateFileRowsWritten,
                metrics.StateFileWriteBatches,
                metrics.StateDirectoryRowsWritten,
                stateWriteRatePerSecond,
                metrics.StartingManagedHeapBytes,
                completedManagedHeapBytes,
                metrics.PeakManagedHeapBytes,
                completedManagedHeapBytes - metrics.StartingManagedHeapBytes,
                options.InitialVirtualFilesPopulationQueueCapacity,
                options.InitialVirtualFilesPlaceholderConcurrency,
                options.InitialVirtualFilesPlaceholderBatchSize,
                options.InitialVirtualFilesStateBatchSize,
                result.Activities.Count,
                result.TotalActivityCount,
                result.IsActivityListTruncated);
        }
    }
}
