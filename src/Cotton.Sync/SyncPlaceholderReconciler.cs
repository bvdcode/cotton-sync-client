// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal class SyncPlaceholderReconciler(
        SyncFileMaterializer fileMaterializer,
        SyncFileConflictResolver conflictResolver,
        SyncFileDeleteExecutor fileDeleteExecutor,
        SyncFileUploadExecutor fileUploadExecutor,
        ISyncStateStore stateStore)
    {
        public async Task<bool> TryMaterializeIncompletePlaceholderAsync(SyncFileReconciliationContext context)
        {
            if (context.SyncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || context.Local is not { IsCloudFilesOnlineOnlyPlaceholder: true }
                || context.Remote is null
                || !IsIncompleteOnlineOnlyPlaceholderBaseline(context.State))
            {
                return false;
            }

            await fileMaterializer.MaterializeAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.RelativePath,
                    context.Remote.File,
                    context.CancellationToken,
                    context.State.PlaceholderHydrationState)
                .ConfigureAwait(false);
            return true;
        }

        public async Task<bool> TryReconcileMissingOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            SyncFileChangeState changeState)
        {
            if (!IsOnlineOnlyPlaceholderBaseline(context.SyncPair, context.State))
            {
                return false;
            }

            if (context.Local is null && context.Remote is not null)
            {
                await ReconcileMissingLocalOnlineOnlyPlaceholderAsync(context, changeState.RemoteChanged)
                    .ConfigureAwait(false);
                return true;
            }

            if (!changeState.RemoteDeleted)
            {
                return false;
            }

            await ReconcileRemoteDeletedOnlineOnlyPlaceholderAsync(context).ConfigureAwait(false);
            return true;
        }

        private async Task ReconcileMissingLocalOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            bool remoteChanged)
        {
            if (context.IsExactLocalDelete && remoteChanged)
            {
                await conflictResolver.PreserveAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.RelativePath,
                        null,
                        context.Remote!.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.IsExactLocalDelete)
            {
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
            }

            if (remoteChanged || context.Options.RestoreMissingRemoteOnlyPlaceholders)
            {
                await fileMaterializer.MaterializeAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.RelativePath,
                        context.Remote!.File,
                        context.CancellationToken,
                        context.State.PlaceholderHydrationState)
                    .ConfigureAwait(false);
                return;
            }

            if (!context.Options.Scope.IsFull)
            {
                return;
            }

            SyncActivityReporter.ReportActivity(
                context.Result,
                context.Options,
                SyncActivityKind.Skipped,
                context.RelativePath,
                VirtualFileUserFacingCopy.RemoteOnlyLocalChangeRequiresActionMessage,
                requiresUserAction: true);
        }

        private async Task ReconcileRemoteDeletedOnlineOnlyPlaceholderAsync(SyncFileReconciliationContext context)
        {
            if (context.Local is null)
            {
                await stateStore.DeleteAsync(
                        context.SyncPair.SyncPairId,
                        context.RelativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (IsLocalOnlineOnlyPlaceholderBaseline(context.SyncPair, context.Local, context.State))
            {
                await fileDeleteExecutor.DeleteLocalAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.DeleteGuard,
                        context.RelativePath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await conflictResolver.PreserveAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.RelativePath,
                    context.Local,
                    null,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<bool> TryReconcilePresentOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            SyncFileChangeState changeState)
        {
            if (context.Local is null || context.Remote is null)
            {
                return false;
            }

            if (IsLocalOnlineOnlyPlaceholderBaseline(context.SyncPair, context.Local, context.State))
            {
                if (changeState.RemoteChanged)
                {
                    await fileMaterializer.MaterializeAsync(
                            context.SyncPair,
                            context.Options,
                            context.Result,
                            context.RelativePath,
                            context.Remote.File,
                            context.CancellationToken,
                            context.State.PlaceholderHydrationState)
                        .ConfigureAwait(false);
                }

                return true;
            }

            if (!IsOnlineOnlyPlaceholderBaseline(context.SyncPair, context.State))
            {
                return false;
            }

            await ReconcileHydratedOnlineOnlyPlaceholderAsync(context, changeState.RemoteChanged).ConfigureAwait(false);
            return true;
        }

        private async Task ReconcileHydratedOnlineOnlyPlaceholderAsync(
            SyncFileReconciliationContext context,
            bool remoteChanged)
        {
            if (ContentMatches(context.Local!.ContentHash, context.Remote!.File.ContentHash))
            {
                await stateStore.UpsertAsync(
                        BuildHydratedPlaceholderBaseline(
                            context.SyncPair,
                            context.RelativePath,
                            context.Local,
                            context.Remote.File,
                            context.State),
                        context.CancellationToken)
                    .ConfigureAwait(false);
                SyncActivityReporter.ReportActivity(
                    context.Result,
                    context.Options,
                    SyncActivityKind.Converged,
                    context.RelativePath,
                    "Hydrated placeholder content matches the remote file.");
                return;
            }

            if (!remoteChanged)
            {
                await fileUploadExecutor.UploadAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.RelativePath,
                        context.Local,
                        context.Remote.File,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await conflictResolver.PreserveAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.RelativePath,
                    context.Local,
                    context.Remote.File,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
    }
}
