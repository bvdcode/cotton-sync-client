// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncRunProgressReporter;

namespace Cotton.Sync
{
    internal class InitialVirtualFilesFileBatchProcessor(
        IRemoteFilePlaceholderWriter? placeholderWriter,
        SyncFileMaterializer fileMaterializer,
        ISyncStateStore stateStore,
        ILocalFilePresenceProbe? localFilePresenceProbe)
    {
        public bool SupportsBatchWriting => placeholderWriter is IRemoteFilePlaceholderBatchWriter;

        public void EnqueueInitialVirtualFilesFileBatchWork(
            List<Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>>> pendingFileTasks,
            List<RemoteFileSnapshot> pendingFileBatch,
            SyncPair syncPair,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (pendingFileBatch.Count == 0)
            {
                return;
            }

            RemoteFileSnapshot[] batch = [.. pendingFileBatch];
            pendingFileBatch.Clear();
            pendingFileTasks.Add(CreateInitialVirtualFilesFileBatchWorkAsync(
                syncPair,
                options,
                batch,
                cancellationToken));
        }

        private Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> CreateInitialVirtualFilesFileBatchWorkAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            IReadOnlyList<RemoteFileSnapshot> remoteFiles,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                async () =>
                {
                    if (remoteFiles.Count == 0)
                    {
                        return Array.Empty<InitialVirtualFilesFileWorkResult>();
                    }

                    if (placeholderWriter is IRemoteFilePlaceholderBatchWriter batchWriter)
                    {
                        return await CreateInitialVirtualFilesBatchResultsAsync(
                                syncPair,
                                batchWriter,
                                remoteFiles,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    InitialVirtualFilesFileWorkResult[] results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
                    for (int index = 0; index < remoteFiles.Count; index++)
                    {
                        results[index] = await CreateInitialVirtualFilesFileResultAsync(
                                syncPair,
                                options,
                                remoteFiles[index],
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return results;
                },
                cancellationToken);
        }

        private async Task<IReadOnlyList<InitialVirtualFilesFileWorkResult>> CreateInitialVirtualFilesBatchResultsAsync(
            SyncPair syncPair,
            IRemoteFilePlaceholderBatchWriter batchWriter,
            IReadOnlyList<RemoteFileSnapshot> remoteFiles,
            CancellationToken cancellationToken)
        {
            RemoteFilePlaceholderRequest[] requests = new RemoteFilePlaceholderRequest[remoteFiles.Count];
            for (int index = 0; index < remoteFiles.Count; index++)
            {
                RemoteFileSnapshot remote = remoteFiles[index];
                requests[index] = RemoteFilePlaceholderRequestFactory.Create(
                    syncPair,
                    remote.RelativePath,
                    remote.File);
            }

            try
            {
                IReadOnlyList<RemoteFilePlaceholderBatchResult> batchResults =
                    await batchWriter.CreatePlaceholdersAsync(requests, cancellationToken).ConfigureAwait(false);
                if (batchResults.Count != remoteFiles.Count)
                {
                    throw new InvalidOperationException("Batch placeholder writer returned a different number of results.");
                }

                InitialVirtualFilesFileWorkResult[] results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
                for (int index = 0; index < remoteFiles.Count; index++)
                {
                    RemoteFileSnapshot remote = remoteFiles[index];
                    RemoteFilePlaceholderBatchResult batchResult = batchResults[index];
                    results[index] = batchResult.Placeholder is null
                        ? new InitialVirtualFilesFileWorkResult(
                            remote.RelativePath,
                            State: null,
                            SyncActivityKind.Skipped,
                            batchResult.UnavailableReason,
                            RequiresUserAction: true,
                            ReportActivity: true)
                        : new InitialVirtualFilesFileWorkResult(
                            remote.RelativePath,
                            BuildPlaceholderBaseline(syncPair, remote.RelativePath, remote.File, batchResult.Placeholder),
                            SyncActivityKind.PlaceholderCreated,
                            Details: null,
                            RequiresUserAction: false,
                            ReportActivity: false);
                }

                return results;
            }
            catch (RemoteFilePlaceholderUnavailableException exception)
            {
                InitialVirtualFilesFileWorkResult[] results = new InitialVirtualFilesFileWorkResult[remoteFiles.Count];
                for (int index = 0; index < remoteFiles.Count; index++)
                {
                    results[index] = new InitialVirtualFilesFileWorkResult(
                        remoteFiles[index].RelativePath,
                        State: null,
                        SyncActivityKind.Skipped,
                        exception.Reason,
                        RequiresUserAction: true,
                        ReportActivity: true);
                }

                return results;
            }
        }

        private async Task<InitialVirtualFilesFileWorkResult> CreateInitialVirtualFilesFileResultAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            RemoteFileSnapshot remote,
            CancellationToken cancellationToken)
        {
            try
            {
                SyncStateEntry? placeholderState = await fileMaterializer.CreatePlaceholderStateAsync(
                        syncPair,
                        options,
                        remote.RelativePath,
                        remote.File,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new InitialVirtualFilesFileWorkResult(
                    remote.RelativePath,
                    placeholderState,
                    SyncActivityKind.PlaceholderCreated,
                    Details: null,
                    RequiresUserAction: false,
                    ReportActivity: false);
            }
            catch (RemoteFilePlaceholderUnavailableException exception)
            {
                return new InitialVirtualFilesFileWorkResult(
                    remote.RelativePath,
                    State: null,
                    SyncActivityKind.Skipped,
                    exception.Reason,
                    RequiresUserAction: true,
                    ReportActivity: true);
            }
        }

        public async Task CompleteInitialVirtualFilesFileWorkBatchAsync(
            IReadOnlyList<InitialVirtualFilesFileWorkResult> workResults,
            List<SyncStateEntry> pendingFileStates,
            InitialVirtualFilesPopulationContext context)
        {
            foreach (InitialVirtualFilesFileWorkResult workResult in workResults)
            {
                await CompleteInitialVirtualFilesFileWorkAsync(
                        workResult,
                        pendingFileStates,
                        context)
                    .ConfigureAwait(false);
            }
        }

        public async Task CompleteInitialVirtualFilesFileWorkAsync(
            InitialVirtualFilesFileWorkResult workResult,
            List<SyncStateEntry> pendingFileStates,
            InitialVirtualFilesPopulationContext context)
        {
            context.Metrics.RecordFileWorkResult(workResult);

            if (workResult.State is not null)
            {
                pendingFileStates.Add(workResult.State);
                if (pendingFileStates.Count >= context.Options.InitialVirtualFilesStateBatchSize)
                {
                    int flushedFileRows = await FlushInitialVirtualFilesStateBatchAsync(
                            pendingFileStates,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    context.Metrics.RecordFileStateWrite(flushedFileRows);
                }
            }

            int completedFiles = context.Metrics.RecordCompletedFile();
            if (ShouldReportInitialVirtualFilesFileProgress(workResult))
            {
                InitialVirtualFilesProgress.Report(context, workResult.RelativePath);
            }
            if (workResult.ReportActivity)
            {
                SyncActivityReporter.ReportActivity(
                    context.Result,
                    context.Options,
                    workResult.ActivityKind,
                    workResult.RelativePath,
                    workResult.Details,
                    workResult.RequiresUserAction,
                    publishActivityProgress: true);
            }

            await YieldAfterLargeBatchAsync(
                    context.Options,
                    InitialVirtualFilesProgress.GetItemCount(completedFiles, context.Metrics.CompletedDirectories),
                    Math.Max(
                        InitialVirtualFilesProgress.GetItemCount(completedFiles, context.Metrics.CompletedDirectories),
                        InitialVirtualFilesProgress.GetItemCount(
                            context.Metrics.DiscoveredFiles,
                            context.Metrics.DiscoveredDirectories)),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<int> FlushInitialVirtualFilesStateBatchAsync(
            List<SyncStateEntry> pendingFileStates,
            CancellationToken cancellationToken)
        {
            if (pendingFileStates.Count == 0)
            {
                return 0;
            }

            int writtenRows = pendingFileStates.Count;
            await stateStore.UpsertManyAsync(pendingFileStates, cancellationToken).ConfigureAwait(false);
            pendingFileStates.Clear();
            return writtenRows;
        }

        public InitialVirtualFilesFileWorkResult? TryCreateCurrentInitialVirtualFilesFileWorkResult(
            SyncPair syncPair,
            RemoteFileSnapshot remote,
            InitialVirtualFilesStreamingPlan streamingPlan)
        {
            if (!streamingPlan.SkipCurrentPlaceholders)
            {
                return null;
            }

            string key = SyncPath.ToKey(remote.RelativePath);
            if (streamingPlan.CurrentPlaceholderBaselineByPath.TryGetValue(
                    key,
                    out InitialVirtualFilesPlaceholderBaseline baseline)
                && HasRemoteFileBaseline(baseline)
                && RemoteMatchesBaseline(remote.File, baseline)
                && localFilePresenceProbe?.FileExists(syncPair.LocalRootPath, remote.RelativePath) == true)
            {
                return new InitialVirtualFilesFileWorkResult(
                    remote.RelativePath,
                    State: null,
                    SyncActivityKind.Skipped,
                    Details: null,
                    RequiresUserAction: false,
                    ReportActivity: false);
            }

            return null;
        }

        private static bool ShouldReportInitialVirtualFilesFileProgress(InitialVirtualFilesFileWorkResult workResult)
        {
            return workResult.State is not null
                || workResult.ReportActivity
                || workResult.ActivityKind != SyncActivityKind.Skipped;
        }
    }
}
