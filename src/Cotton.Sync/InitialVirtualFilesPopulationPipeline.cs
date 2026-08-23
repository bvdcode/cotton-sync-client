// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Threading.Channels;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesPopulationPipeline(
        IRemoteTreeStreamingCrawler? remoteStreamingCrawler,
        InitialVirtualFilesFileBatchProcessor fileBatchProcessor,
        IRemoteDirectoryTreePopulationObserver? directoryTreePopulationObserver,
        SyncDirectoryReconciler directoryReconciler,
        SyncFileDeleteExecutor fileDeleteExecutor)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static async Task RunAsync(
            Task producer,
            Task consumer,
            Task heartbeat,
            ChannelWriter<InitialVirtualFilesPopulationItem> writer,
            CancellationTokenSource streamingCancellation,
            CancellationTokenSource heartbeatCancellation)
        {
            try
            {
                Task firstCompleted = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
                if (firstCompleted.IsFaulted || firstCompleted.IsCanceled)
                {
                    await streamingCancellation.CancelAsync().ConfigureAwait(false);
                    writer.TryComplete(firstCompleted.Exception);
                }

                await Task.WhenAll(producer, consumer).ConfigureAwait(false);
            }
            finally
            {
                await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
                await InitialVirtualFilesHeartbeatLogger.IgnoreExpectedCancellationAsync(heartbeat, heartbeatCancellation.Token).ConfigureAwait(false);
            }
        }

        public async Task ProduceAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            Channel<InitialVirtualFilesPopulationItem> channel,
            IRemoteTreeStreamSink sink,
            IProgress<RemoteTreeScanProgress> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                await remoteStreamingCrawler!
                    .CrawlStreamingAsync(
                        syncPair.RemoteRootNodeId,
                        sink,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
                throw;
            }
        }

        public async Task ConsumeAsync(
            InitialVirtualFilesPopulationContext context)
        {
            int placeholderBatchSize = fileBatchProcessor.SupportsBatchWriting
                ? context.Options.InitialVirtualFilesPlaceholderBatchSize
                : 1;
            InitialVirtualFilesConsumerState state = new(
                context.Options.InitialVirtualFilesStateBatchSize,
                placeholderBatchSize,
                context.Options.InitialVirtualFilesPlaceholderConcurrency,
                directoryTreePopulationObserver is not null,
                context.StreamingPlan.CurrentPlaceholderBaselineByPath.Count > 0,
                PathComparer);

            try
            {
                await foreach (InitialVirtualFilesPopulationItem item in context.Reader
                                   .ReadAllAsync(context.CancellationToken)
                                   .ConfigureAwait(false))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    switch (item)
                    {
                        case InitialVirtualFilesDirectoryPopulationItem directoryItem:
                            await ProcessInitialVirtualFilesDirectoryAsync(context, state, directoryItem.Directory)
                                .ConfigureAwait(false);
                            break;

                        case InitialVirtualFilesFilePopulationItem fileItem:
                            await ProcessInitialVirtualFilesFileAsync(context, state, fileItem.File)
                                .ConfigureAwait(false);
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"Unsupported initial virtual-files population item type '{item.GetType().FullName}'.");
                    }
                }

                await FinalizeInitialVirtualFilesPopulationAsync(context, state).ConfigureAwait(false);
            }
            finally
            {
                await FlushInitialVirtualFilesPopulationStateAsync(context, state).ConfigureAwait(false);
            }
        }

        private async Task ProcessInitialVirtualFilesDirectoryAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state,
            RemoteDirectorySnapshot directory)
        {
            RecordInitialVirtualFilesRemotePath(state, directory.RelativePath);
            fileBatchProcessor.EnqueueInitialVirtualFilesFileBatchWork(
                state.PendingFileTasks,
                state.PendingFileBatch,
                context.SyncPair,
                context.Options,
                context.CancellationToken);
            await DrainCompletedInitialVirtualFilesAsync(
                    state.PendingFileTasks,
                    state.PendingFileStates,
                    context,
                    waitForOne: false)
                .ConfigureAwait(false);
            await directoryReconciler.CreateRemoteBackedLocalDirectoryAsync(
                    context.SyncPair,
                    directory.RelativePath,
                    directory.Node,
                    context.CancellationToken)
                .ConfigureAwait(false);
            RecordInitialVirtualFilesDirectoryFinalization(context.SyncPair, state, directory);
            state.PendingDirectoryStates.Add(BuildDirectoryBaseline(
                context.SyncPair,
                directory.RelativePath,
                directory.Node));
            if (state.PendingDirectoryStates.Count >= context.Options.InitialVirtualFilesStateBatchSize)
            {
                int flushedDirectoryRows = await fileBatchProcessor.FlushInitialVirtualFilesStateBatchAsync(
                        state.PendingDirectoryStates,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                context.Metrics.RecordDirectoryStateWrite(flushedDirectoryRows);
            }

            context.Metrics.RecordCompletedDirectory();
            InitialVirtualFilesProgress.Report(context, directory.RelativePath);
        }

        private static void RecordInitialVirtualFilesDirectoryFinalization(
            SyncPair syncPair,
            InitialVirtualFilesConsumerState state,
            RemoteDirectorySnapshot directory)
        {
            if (state.DirectoryFinalizationRequests is null)
            {
                return;
            }

            RemoteDirectoryMaterializationRequest request =
                SyncDirectoryReconciler.CreateRemoteDirectoryMaterializationRequest(
                syncPair,
                directory.RelativePath,
                directory.Node);
            state.DirectoryFinalizationRequests[SyncPath.ToKey(request.RelativePath)] = request;
        }

        private async Task ProcessInitialVirtualFilesFileAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state,
            RemoteFileSnapshot file)
        {
            string normalizedPath = RecordInitialVirtualFilesRemotePath(state, file.RelativePath);
            state.StreamedRemoteFilePaths?.Add(normalizedPath);
            InitialVirtualFilesFileWorkResult? currentPlaceholderWorkResult =
                fileBatchProcessor.TryCreateCurrentInitialVirtualFilesFileWorkResult(context.SyncPair, file, context.StreamingPlan);
            if (currentPlaceholderWorkResult is not null)
            {
                await fileBatchProcessor.CompleteInitialVirtualFilesFileWorkAsync(
                        currentPlaceholderWorkResult,
                        state.PendingFileStates,
                        context)
                    .ConfigureAwait(false);
                return;
            }

            state.PendingFileBatch.Add(file);
            int placeholderBatchSize = fileBatchProcessor.SupportsBatchWriting
                ? context.Options.InitialVirtualFilesPlaceholderBatchSize
                : 1;
            if (state.PendingFileBatch.Count >= placeholderBatchSize)
            {
                fileBatchProcessor.EnqueueInitialVirtualFilesFileBatchWork(
                    state.PendingFileTasks,
                    state.PendingFileBatch,
                    context.SyncPair,
                    context.Options,
                    context.CancellationToken);
            }

            if (state.PendingFileTasks.Count >= context.Options.InitialVirtualFilesPlaceholderConcurrency)
            {
                await DrainCompletedInitialVirtualFilesAsync(
                        state.PendingFileTasks,
                        state.PendingFileStates,
                        context,
                        waitForOne: true)
                    .ConfigureAwait(false);
            }
        }

        private static string RecordInitialVirtualFilesRemotePath(
            InitialVirtualFilesConsumerState state,
            string relativePath)
        {
            string normalizedPath = SyncPath.Normalize(relativePath);
            if (state.StreamedRemotePaths.TryGetValue(normalizedPath, out string? existingPath))
            {
                throw new SyncPathCollisionException(existingPath, normalizedPath);
            }

            state.StreamedRemotePaths.Add(normalizedPath);
            return normalizedPath;
        }

        private async Task FinalizeInitialVirtualFilesPopulationAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state)
        {
            fileBatchProcessor.EnqueueInitialVirtualFilesFileBatchWork(
                state.PendingFileTasks,
                state.PendingFileBatch,
                context.SyncPair,
                context.Options,
                context.CancellationToken);
            while (state.PendingFileTasks.Count > 0)
            {
                await DrainCompletedInitialVirtualFilesAsync(
                        state.PendingFileTasks,
                        state.PendingFileStates,
                        context,
                        waitForOne: true)
                    .ConfigureAwait(false);
            }

            await DeleteMissingInitialVirtualFilesRemoteDeletesAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.StreamingPlan,
                    state.StreamedRemoteFilePaths,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await FlushInitialVirtualFilesPopulationStateAsync(context, state).ConfigureAwait(false);
            await FinalizeInitialVirtualFilesDirectoryTreeAsync(context, state).ConfigureAwait(false);
        }

        private async Task FinalizeInitialVirtualFilesDirectoryTreeAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state)
        {
            if (state.DirectoryFinalizationRequests is not { Count: > 0 } requests
                || directoryTreePopulationObserver is null)
            {
                return;
            }

            int directoriesDiscovered = Math.Max(requests.Count, context.Metrics.DiscoveredDirectories);
            SyncRunProgressReporter.ReportRunProgress(
                context.Options,
                SyncRunProgressStage.FinalizingCloudFiles,
                0,
                directoriesDiscovered,
                null,
                context.StartedAtUtc);
            await directoryTreePopulationObserver
                .AfterDirectoryTreePopulationAsync(requests.Values.ToArray(), context.CancellationToken)
                .ConfigureAwait(false);
            SyncRunProgressReporter.ReportRunProgress(
                context.Options,
                SyncRunProgressStage.FinalizingCloudFiles,
                requests.Count,
                directoriesDiscovered,
                null,
                context.StartedAtUtc,
                isCompleted: true);
        }

        private async Task FlushInitialVirtualFilesPopulationStateAsync(
            InitialVirtualFilesPopulationContext context,
            InitialVirtualFilesConsumerState state)
        {
            int flushedFileRows = await fileBatchProcessor.FlushInitialVirtualFilesStateBatchAsync(
                    state.PendingFileStates,
                    context.CancellationToken)
                .ConfigureAwait(false);
            context.Metrics.RecordFileStateWrite(flushedFileRows);
            int flushedDirectoryRows = await fileBatchProcessor.FlushInitialVirtualFilesStateBatchAsync(
                    state.PendingDirectoryStates,
                    context.CancellationToken)
                .ConfigureAwait(false);
            context.Metrics.RecordDirectoryStateWrite(flushedDirectoryRows);
        }

        private async Task DeleteMissingInitialVirtualFilesRemoteDeletesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            InitialVirtualFilesStreamingPlan streamingPlan,
            IReadOnlySet<string>? streamedRemoteFilePaths,
            CancellationToken cancellationToken)
        {
            if (streamingPlan.CurrentPlaceholderBaselineByPath.Count == 0)
            {
                return;
            }

            if (streamedRemoteFilePaths is null)
            {
                throw new InvalidOperationException("Remote file paths were not tracked for resume finalization.");
            }

            List<InitialVirtualFilesPlaceholderBaseline> missingBaselines = [];
            foreach (InitialVirtualFilesPlaceholderBaseline baseline in streamingPlan.CurrentPlaceholderBaselineByPath.Values)
            {
                if (!streamedRemoteFilePaths.Contains(baseline.RelativePath))
                {
                    missingBaselines.Add(baseline);
                }
            }

            if (missingBaselines.Count == 0)
            {
                return;
            }

            SyncDeleteGuard deleteGuard = new(options, plannedLocalDeletes: missingBaselines.Count, []);
            foreach (InitialVirtualFilesPlaceholderBaseline baseline in missingBaselines)
            {
                await fileDeleteExecutor.DeleteLocalAsync(
                        syncPair,
                        options,
                        result,
                        deleteGuard,
                        baseline.RelativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task DrainCompletedInitialVirtualFilesAsync(
            List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>> pendingFileTasks,
            List<SyncStateEntry> pendingFileStates,
            InitialVirtualFilesPopulationContext context,
            bool waitForOne)
        {
            if (pendingFileTasks.Count == 0)
            {
                return;
            }

            if (waitForOne)
            {
                Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> completedTask =
                    await Task.WhenAny(pendingFileTasks).ConfigureAwait(false);
                pendingFileTasks.Remove(completedTask);
                await fileBatchProcessor.CompleteInitialVirtualFilesFileWorkBatchAsync(
                        await completedTask.ConfigureAwait(false),
                        pendingFileStates,
                        context)
                    .ConfigureAwait(false);
            }

            for (int index = pendingFileTasks.Count - 1; index >= 0; index--)
            {
                Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> task = pendingFileTasks[index];
                if (!task.IsCompleted)
                {
                    continue;
                }

                pendingFileTasks.RemoveAt(index);
                await fileBatchProcessor.CompleteInitialVirtualFilesFileWorkBatchAsync(
                        await task.ConfigureAwait(false),
                        pendingFileStates,
                        context)
                    .ConfigureAwait(false);
            }
        }
    }
}
