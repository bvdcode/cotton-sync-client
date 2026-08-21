// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.RemoteSyncErrorClassifier;

namespace Cotton.Sync
{
    internal class SyncFileDeleteExecutor(
        IRemoteFileSynchronizer remoteFiles,
        ILocalFileSyncWriter localWriter,
        ISyncStateStore stateStore,
        SyncRemoteFileTransfer fileTransfer,
        SyncFileConflictResolver conflictResolver)
    {
        public async Task DeleteRemoteAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            if (!deleteGuard.CanDeleteRemote(out string? details))
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    details,
                    requiresUserAction: true);
                return;
            }

            try
            {
                await remoteFiles.DeleteFileAsync(
                    remoteFile.Id,
                    options.DeleteRemotePermanently,
                    remoteFile.ETag,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsPreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await fileTransfer
                    .FindLatestRemoteFileAsync(syncPair, relativePath, cancellationToken)
                    .ConfigureAwait(false);
                await conflictResolver.PreserveAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local: null,
                    remoteFile: latestRemoteFile ?? remoteFile,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.ReportActivity(
                result,
                options,
                SyncActivityKind.DeletedRemote,
                relativePath,
                details: null);
        }

        public async Task DeleteLocalAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            SyncDeleteGuard deleteGuard,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!deleteGuard.CanDeleteLocal(out string? details))
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    details,
                    requiresUserAction: true);
                return;
            }

            await localWriter.DeleteFileAsync(syncPair.LocalRootPath, relativePath, cancellationToken)
                .ConfigureAwait(false);
            await stateStore.DeleteAsync(syncPair.SyncPairId, relativePath, cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.ReportActivity(
                result,
                options,
                SyncActivityKind.DeletedLocal,
                relativePath,
                details: null);
        }
    }
}
