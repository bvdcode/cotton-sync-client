// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.SyncDeletePlanner;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class SyncRunCoordinator(
        SyncTreeScanner treeScanner,
        SyncStateSnapshotLoader stateSnapshotLoader,
        ScopedVirtualFilesDirectoryRenamePlanner scopedDirectoryRenamePlanner,
        RemoteDirectoryMoveCoordinator remoteDirectoryMoveCoordinator,
        SyncDirectoryReconciler directoryReconciler,
        SyncStateFileHashLoader stateFileHashLoader,
        SyncOnlineOnlyPlaceholderMoveCoordinator onlineOnlyPlaceholderMoveCoordinator,
        SyncLocalFileMoveCoordinator localFileMoveCoordinator,
        ScopedVirtualFilesDirectoryDeleteExecutor scopedDirectoryDeleteExecutor,
        SyncDirectoryDeleteReconciler directoryDeleteReconciler,
        ILogger logger)
    {
        public async Task<SyncRunContext> PrepareAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            DateTime startedAtUtc,
            CancellationToken cancellationToken)
        {
            SyncTreeLookups treeLookups = await treeScanner.ScanAsync(
                    syncPair,
                    options,
                    startedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            (Dictionary<string, SyncStateEntry> directoryStateByPath, Dictionary<string, SyncStateEntry> fileStateByPath) =
                await stateSnapshotLoader.LoadAsync(
                        syncPair.SyncPairId,
                        options,
                        treeLookups,
                        cancellationToken)
                    .ConfigureAwait(false);
            ScopedVirtualFilesDirectoryRenamePlan? scopedDirectoryRename =
                await scopedDirectoryRenamePlanner.ExpandAsync(
                        syncPair,
                        options,
                        treeLookups,
                        directoryStateByPath,
                        fileStateByPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateSyncTreePathKinds(treeLookups);
            return new SyncRunContext(
                syncPair,
                options,
                new SyncRunResult(),
                treeLookups,
                directoryStateByPath,
                fileStateByPath,
                scopedDirectoryRename,
                startedAtUtc,
                cancellationToken);
        }

        private static void ValidateSyncTreePathKinds(SyncTreeLookups treeLookups)
        {
            ThrowIfPathKindCollisions(
                treeLookups.LocalDirectoriesByPath,
                treeLookups.LocalFilesByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
            ThrowIfPathKindCollisions(
                treeLookups.RemoteDirectoriesByPath,
                treeLookups.RemoteFilesByPath,
                directory => directory.RelativePath,
                file => file.RelativePath);
        }

        public async Task<IReadOnlyList<string>> ReconcileDirectoriesAsync(SyncRunContext context)
        {
            await remoteDirectoryMoveCoordinator.CoalesceAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalDirectoriesByPath,
                    context.RemoteDirectoriesByPath,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.DirectoryStateByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> directoryPathKeys = BuildDirectoryPathKeys(
                context.LocalDirectoriesByPath.Keys,
                context.RemoteDirectoriesByPath.Keys,
                context.DirectoryStateByPath.Keys);
            SyncRunProgressReporter.ReportRunProgress(
                context.Options,
                SyncRunProgressStage.ReconcilingDirectories,
                0,
                directoryPathKeys.Count,
                null,
                context.StartedAtUtc);
            DirectoryReconciliationContext directoryReconciliation = new(
                context.SyncPair,
                context.Options,
                context.Result,
                directoryPathKeys,
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath,
                context.TreeLookups.RemoteRootNode,
                context.StartedAtUtc,
                context.CancellationToken);
            await directoryReconciler.ReconcileWithoutBaselineAsync(directoryReconciliation).ConfigureAwait(false);
            return directoryPathKeys;
        }

        public async Task<SyncDeletePlan> BuildDeletePlanAsync(SyncRunContext context)
        {
            await stateFileHashLoader.LoadAsync(
                    context.LocalFilesByPath,
                    context.FileStateByPath,
                    context.Options,
                    context.Result,
                    context.StartedAtUtc,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await onlineOnlyPlaceholderMoveCoordinator.CoalesceAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            await localFileMoveCoordinator.CoalesceAsync(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            if (context.ScopedDirectoryRename is not null)
            {
                await scopedDirectoryDeleteExecutor.DeleteConfirmedScopedVirtualFilesDirectoryRenameSourceAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        context.ScopedDirectoryRename,
                        context.RemoteDirectoriesByPath,
                        context.RemoteFilesByPath,
                        context.DirectoryStateByPath,
                        context.FileStateByPath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            bool hasLocalDirectoryDeleteCandidates = HasLocalDirectoryDeleteCandidates(
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath);
            bool hasRemoteDirectoryDeleteCandidates = HasRemoteDirectoryDeleteCandidates(
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath);
            bool hasStaleDirectoryState = HasStaleDirectoryState(
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath);
            DirectoryContentIndex localDirectoryContentIndex = hasLocalDirectoryDeleteCandidates
                ? DirectoryContentIndex.Create(context.LocalDirectoriesByPath.Keys, context.LocalFilesByPath.Keys)
                : DirectoryContentIndex.Empty;
            DirectoryContentIndex remoteDirectoryContentIndex = hasRemoteDirectoryDeleteCandidates
                ? DirectoryContentIndex.Create(context.RemoteDirectoriesByPath.Keys, context.RemoteFilesByPath.Keys)
                : DirectoryContentIndex.Empty;
            ScopedVirtualFilesDirectoryDeletePlan? scopedDirectoryDelete =
                ScopedVirtualFilesDirectoryDeletePlanner.Build(
                    context.SyncPair,
                    context.Options,
                    new ScopedVirtualFilesDirectoryDeleteContext(
                        context.LocalDirectoriesByPath,
                        context.RemoteDirectoriesByPath,
                        context.LocalFilesByPath,
                        context.RemoteFilesByPath,
                        context.DirectoryStateByPath,
                        context.FileStateByPath));
            IReadOnlySet<string>? scopedFileDeleteKeys = context.Options.Scope.IsFull
                ? null
                : BuildExactScopedPathKeys(context.Options.Scope.LocalChangedPaths);
            IReadOnlySet<string>? scopedDirectoryDeleteKeys = context.Options.Scope.IsFull
                ? null
                : BuildExactScopedPathKeys(context.Options.Scope.LocalChangedPaths);
            IReadOnlySet<string> scopedLocalDeletedFileKeys =
                BuildExactScopedPathKeys(context.Options.Scope.LocalDeletedPaths);
            if (scopedDirectoryDelete is not null)
            {
                scopedFileDeleteKeys = AddScopedPathKeys(scopedFileDeleteKeys!, scopedDirectoryDelete.FileKeys);
                scopedLocalDeletedFileKeys = AddScopedPathKeys(
                    scopedLocalDeletedFileKeys,
                    scopedDirectoryDelete.FileKeys);
            }
            SyncDeleteGuard deleteGuard = BuildDeleteGuard(
                context.Options,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath,
                context.LocalDirectoriesByPath,
                context.RemoteDirectoriesByPath,
                context.DirectoryStateByPath,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex,
                scopedFileDeleteKeys,
                scopedDirectoryDeleteKeys,
                scopedLocalDeletedFileKeys,
                scopedDirectoryDelete);
            bool hasMissingRemoteOnlyPlaceholder = HasMissingRemoteOnlyPlaceholder(
                context.SyncPair,
                context.LocalFilesByPath,
                context.RemoteFilesByPath,
                context.FileStateByPath);
            return new SyncDeletePlan(
                deleteGuard,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex,
                scopedFileDeleteKeys,
                scopedDirectoryDeleteKeys,
                scopedLocalDeletedFileKeys,
                scopedDirectoryDelete,
                hasLocalDirectoryDeleteCandidates,
                hasLocalDirectoryDeleteCandidates || hasRemoteDirectoryDeleteCandidates || hasStaleDirectoryState,
                hasMissingRemoteOnlyPlaceholder);
        }

        public async Task ReconcilePlannedDirectoryDeletesAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            IReadOnlyList<string> directoryPathKeys)
        {
            if (!deletePlan.RequiresDirectoryReconciliation)
            {
                return;
            }

            DirectoryDeleteContext directoryDeletes = new(
                    context.SyncPair,
                    context.Options,
                    context.Result,
                    deletePlan.DeleteGuard,
                    directoryPathKeys,
                    context.LocalDirectoriesByPath,
                    context.RemoteDirectoriesByPath,
                    context.DirectoryStateByPath,
                    context.LocalFilesByPath,
                    context.RemoteFilesByPath,
                    context.FileStateByPath,
                    deletePlan.LocalDirectoryContentIndex,
                    deletePlan.RemoteDirectoryContentIndex,
                    deletePlan.ScopedDirectoryDeleteKeys,
                    deletePlan.ScopedDirectoryDelete?.DirectoryKeys,
                    context.CancellationToken);
            await directoryDeleteReconciler.ReconcileAsync(directoryDeletes).ConfigureAwait(false);
        }

        public async Task CompleteAsync(
            SyncRunContext context,
            SyncDeletePlan deletePlan,
            IReadOnlyList<string> directoryPathKeys,
            SyncFilePhaseResult filePhase)
        {
            if (deletePlan.HasLocalDirectoryDeleteCandidates)
            {
                await directoryDeleteReconciler.ReconcileEmptyLocalDirectoriesAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        deletePlan.DeleteGuard,
                        directoryPathKeys,
                        context.LocalDirectoriesByPath,
                        context.RemoteDirectoriesByPath,
                        context.DirectoryStateByPath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (deletePlan.ScopedDirectoryDelete is not null)
            {
                await scopedDirectoryDeleteExecutor.DeleteConfirmedScopedVirtualFilesDirectorySubtreesAsync(
                        context.SyncPair,
                        context.Options,
                        context.Result,
                        deletePlan.DeleteGuard,
                        deletePlan.ScopedDirectoryDelete,
                        context.RemoteDirectoriesByPath,
                        context.DirectoryStateByPath,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            SyncRunProgressReporter.ReportRunProgress(
                context.Options,
                SyncRunProgressStage.Completed,
                filePhase.FilesCompleted,
                filePhase.PathKeys.Count,
                null,
                context.StartedAtUtc,
                isCompleted: true,
                bytesCompleted: filePhase.PlannedTransferBytesTotal,
                bytesTotal: filePhase.PlannedTransferBytesTotal);
            logger.LogInformation(
                "Completed sync pass for pair {SyncPairId} with {ActivityCount} activities.",
                context.SyncPair.SyncPairId,
                context.Result.TotalActivityCount);
        }
    }
}
