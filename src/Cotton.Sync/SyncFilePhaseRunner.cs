// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.LocalUploadPolicy;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;
using static Cotton.Sync.SyncRunProgressReporter;
using static Cotton.Sync.SyncTransferPlanner;

namespace Cotton.Sync
{
    internal class SyncFilePhaseRunner(SyncFileReconciler fileReconciler)
    {
        public async Task<SyncFilePhaseResult> RunAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan)
        {
            IReadOnlyList<string> pathKeys = BuildPathKeys(
                context.LocalFilesByPath.Keys,
                context.RemoteFilesByPath.Keys,
                context.FileStateByPath.Keys);
            EnsureEnoughLocalFreeSpaceForPlannedDownloads(
                context.SyncPair,
                pathKeys,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            long plannedTransferBytesTotal = CalculatePlannedTransferBytesTotal(
                context.SyncPair,
                pathKeys,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            SyncFileReconciliationProgress progress = new(plannedTransferBytesTotal);
            IReadOnlyDictionary<SyncRunProgressStage, int> fileCountsByStage = CountFileRunProgressStages(
                context,
                pathKeys);
            foreach (string key in pathKeys)
            {
                await ReconcileSyncFileAsync(context, deletePlan, progress, fileCountsByStage, pathKeys.Count, key)
                    .ConfigureAwait(false);
            }

            return new SyncFilePhaseResult(pathKeys, progress.FilesCompleted, plannedTransferBytesTotal);
        }

        private async Task ReconcileSyncFileAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            SyncFileReconciliationProgress progress,
            IReadOnlyDictionary<SyncRunProgressStage, int> fileCountsByStage,
            int fileCount,
            string pathKey)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.LocalFilesByPath.TryGetValue(pathKey, out LocalFileSnapshot? local);
            context.RemoteFilesByPath.TryGetValue(pathKey, out RemoteFileSnapshot? remote);
            context.FileStateByPath.TryGetValue(pathKey, out SyncStateEntry? state);
            string relativePath = local?.RelativePath ?? remote?.RelativePath ?? state?.RelativePath ?? pathKey;
            SyncRunProgressStage progressStage = ResolveFileRunProgressStage(context.SyncPair, local, remote, state);
            int stageFileCount = fileCountsByStage[progressStage];
            long plannedTransferBytes = CalculatePlannedTransferBytes(
                context.SyncPair,
                pathKey,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            ReportSyncFileProgress(context, progress, progressStage, stageFileCount, relativePath);
            if (!context.Result.IsLocalPathDeferred(relativePath))
            {
                try
                {
                    if (state is null)
                    {
                        await fileReconciler.ReconcileWithoutBaselineAsync(
                                context.SyncPair,
                                context.Options,
                                context.Result,
                                relativePath,
                                local,
                                remote,
                                deletePlan.HasMissingRemoteOnlyPlaceholder,
                                context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await fileReconciler.ReconcileWithBaselineAsync(
                                context.SyncPair,
                                context.Options,
                                context.Result,
                                deletePlan.DeleteGuard,
                                deletePlan.ScopedFileDeleteKeys,
                                deletePlan.ScopedLocalDeletedFileKeys,
                                state,
                                relativePath,
                                local,
                                remote,
                                context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (LocalFileUnavailableException exception)
                {
                    ReportUnavailable(context.Result, context.Options, relativePath, exception);
                }
            }

            progress.CompleteFile(progressStage, plannedTransferBytes);
            ReportSyncFileProgress(context, progress, progressStage, stageFileCount, relativePath);
            await YieldAfterLargeBatchAsync(
                    context.Options,
                    progress.FilesCompleted,
                    fileCount,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static void ReportSyncFileProgress(
            SyncRunContext context,
            SyncFileReconciliationProgress progress,
            SyncRunProgressStage stage,
            int fileCount,
            string relativePath)
        {
            DateTime? lastReportedAtUtc = progress.GetLastReportedAtUtc(stage);
            ReportItemRunProgress(
                context.Options,
                stage,
                progress.GetFilesCompleted(stage),
                fileCount,
                relativePath,
                context.StartedAtUtc,
                ref lastReportedAtUtc,
                bytesCompleted: progress.CompletedTransferBytes,
                bytesTotal: progress.PlannedTransferBytesTotal);
            progress.SetLastReportedAtUtc(stage, lastReportedAtUtc);
        }

        private static IReadOnlyDictionary<SyncRunProgressStage, int> CountFileRunProgressStages(
            SyncRunContext context,
            IReadOnlyList<string> pathKeys)
        {
            Dictionary<SyncRunProgressStage, int> fileCountsByStage = [];
            foreach (string pathKey in pathKeys)
            {
                context.LocalFilesByPath.TryGetValue(pathKey, out LocalFileSnapshot? local);
                context.RemoteFilesByPath.TryGetValue(pathKey, out RemoteFileSnapshot? remote);
                context.FileStateByPath.TryGetValue(pathKey, out SyncStateEntry? state);
                SyncRunProgressStage stage = ResolveFileRunProgressStage(context.SyncPair, local, remote, state);
                fileCountsByStage[stage] = fileCountsByStage.GetValueOrDefault(stage) + 1;
            }

            return fileCountsByStage;
        }

        private static SyncRunProgressStage ResolveFileRunProgressStage(
            SyncPair syncPair,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            SyncStateEntry? state)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || local is not null
                || remote is null)
            {
                return SyncRunProgressStage.ReconcilingFiles;
            }

            if (state is null
                || (IsOnlineOnlyPlaceholderBaseline(syncPair, state)
                    && !RemoteMatchesBaseline(remote.File, state)))
            {
                return SyncRunProgressStage.CreatingPlaceholders;
            }

            return SyncRunProgressStage.ReconcilingFiles;
        }
    }
}
