// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncDeletePlanner;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal class SyncFileReconciler(
        SyncFileMaterializer fileMaterializer,
        SyncFileUploadExecutor fileUploadExecutor,
        SyncFileConflictResolver conflictResolver,
        SyncPlaceholderReconciler placeholderReconciler,
        SyncFileDeleteExecutor fileDeleteExecutor,
        ISyncStateStore stateStore,
        SyncLocalContentHashResolver contentHashResolver)
    {
        public async Task ReconcileWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            bool blockLocalOnlyUploads,
            CancellationToken cancellationToken)
        {
            if (local is null)
            {
                if (remote is not null)
                {
                    await fileMaterializer.MaterializeAsync(
                            syncPair,
                            options,
                            result,
                            relativePath,
                            remote.File,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            if (remote is null)
            {
                await ReconcileLocalOnlyWithoutBaselineAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        blockLocalOnlyUploads,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await ReconcileLocalAndRemoteWithoutBaselineAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local,
                    remote.File,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task ReconcileLocalOnlyWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            bool blockLocalOnlyUploads,
            CancellationToken cancellationToken)
        {
            if (blockLocalOnlyUploads)
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    "Local upload skipped because a Windows virtual-files placeholder change in the same sync pass requires review.");
                return;
            }

            await fileUploadExecutor.UploadAsync(syncPair, options, result, relativePath, local, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task ReconcileLocalAndRemoteWithoutBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && local.IsCloudFilesOnlineOnlyPlaceholder)
            {
                await fileMaterializer.MaterializeAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await contentHashResolver.EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
            if (!ContentMatches(local.ContentHash, remoteFile.ContentHash))
            {
                await conflictResolver.PreserveAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remoteFile),
                    cancellationToken)
                .ConfigureAwait(false);
            if (ShouldFinalizeConvergedLocalFile(syncPair, local))
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Converged,
                    relativePath,
                    "Local and remote content already matched.");
            }
        }

        public async Task ReconcileWithBaselineAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            IReadOnlySet<string>? scopedFileDeleteKeys,
            IReadOnlySet<string> scopedLocalDeletedFileKeys,
            SyncStateEntry state,
            string relativePath,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            CancellationToken cancellationToken)
        {
            SyncFileReconciliationContext context = new SyncFileReconciliationContext(
                syncPair,
                options,
                result,
                deleteGuard,
                scopedFileDeleteKeys,
                scopedLocalDeletedFileKeys,
                state,
                relativePath,
                local,
                remote,
                cancellationToken);
            if (await placeholderReconciler.TryMaterializeIncompletePlaceholderAsync(context).ConfigureAwait(false))
            {
                return;
            }

            await EnsureReconciliationLocalContentHashAsync(context).ConfigureAwait(false);
            SyncFileChangeState changeState = CreateFileChangeState(state, local, remote);
            if (IsDeleteOutsideScope(context, changeState))
            {
                return;
            }

            if (await TryReconcileMissingTrackedFileAsync(context).ConfigureAwait(false)
                || await placeholderReconciler.TryReconcileMissingOnlineOnlyPlaceholderAsync(context, changeState)
                    .ConfigureAwait(false)
                || await placeholderReconciler.TryReconcilePresentOnlineOnlyPlaceholderAsync(context, changeState)
                    .ConfigureAwait(false)
                || await TryReconcileConvergedFileAsync(context).ConfigureAwait(false))
            {
                return;
            }

            SyncFileChangeKind changeKind = ResolveTrackedFileChange(changeState);
            await ReconcileTrackedFileChangeAsync(context, changeKind).ConfigureAwait(false);
        }

        private async Task EnsureReconciliationLocalContentHashAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is null)
            {
                return;
            }

            await contentHashResolver.EnsureForBaselineComparisonAsync(
                    context.Local,
                    context.State,
                    context.Options,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        private static bool IsDeleteOutsideScope(
            SyncFileReconciliationContext context,
            SyncFileChangeState changeState)
        {
            return (changeState.LocalDeleted || changeState.RemoteDeleted)
                && !IsScopedDeleteAllowed(context.ScopedFileDeleteKeys, context.PathKey);
        }

        private async Task<bool> TryReconcileMissingTrackedFileAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is not null || context.Remote is not null)
            {
                return false;
            }

            await stateStore.DeleteAsync(
                    context.SyncPair.SyncPairId,
                    context.RelativePath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        private async Task<bool> TryReconcileConvergedFileAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is null
                || context.Remote is null
                || !ContentMatches(context.Local.ContentHash, context.Remote.File.ContentHash))
            {
                return false;
            }

            if (!BaselineMatchesCurrentFile(
                    context.SyncPair,
                    context.RelativePath,
                    context.State,
                    context.Local,
                    context.Remote.File))
            {
                await stateStore.UpsertAsync(
                        BuildBaseline(
                            context.SyncPair,
                            context.RelativePath,
                            context.Local.ContentHash,
                            context.Local.LastWriteUtc,
                            context.Local.SizeBytes,
                            context.Remote.File),
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (ShouldFinalizeConvergedLocalFile(context.SyncPair, context.Local))
            {
                SyncActivityReporter.ReportActivity(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Converged,
                    context.RelativePath,
                    "Local and remote content are synchronized.");
            }

            return true;
        }

        private async Task ReconcileTrackedFileChangeAsync(
            SyncFileReconciliationContext context,
            SyncFileChangeKind changeKind)
        {
            switch (changeKind)
            {
                case SyncFileChangeKind.None:
                    return;
                case SyncFileChangeKind.DeleteState:
                    await stateStore.DeleteAsync(
                            context.SyncPair.SyncPairId,
                            context.RelativePath,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.DeleteLocal:
                    await fileDeleteExecutor.DeleteLocalAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.DeleteGuard,
                            context.RelativePath,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.DeleteRemote:
                    await fileDeleteExecutor.DeleteRemoteAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.DeleteGuard,
                            context.RelativePath,
                            context.Remote!.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.Upload:
                    await fileUploadExecutor.UploadAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Local!,
                            context.Remote?.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.Download:
                    await fileMaterializer.DownloadAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Remote!.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                case SyncFileChangeKind.Conflict:
                    await conflictResolver.PreserveAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Local,
                            context.Remote?.File,
                            context.CancellationToken)
                        .ConfigureAwait(false);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null);
            }
        }
    }
}
