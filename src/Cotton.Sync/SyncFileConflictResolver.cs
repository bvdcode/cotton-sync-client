// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncTransferPlanner;

namespace Cotton.Sync
{
    internal class SyncFileConflictResolver(
        SyncLocalContentHashResolver contentHashResolver,
        ILocalFileSyncWriter localWriter,
        SyncRemoteFileTransfer fileTransfer,
        ISyncStateStore stateStore)
    {
        public async Task PreserveAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot? local,
            NodeFileManifestDto? remoteFile,
            CancellationToken cancellationToken)
        {
            string? details = null;
            if (local is not null && remoteFile is not null)
            {
                details = await PreserveDivergedConflictAsync(
                        syncPair,
                        options,
                        relativePath,
                        local,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (local is not null)
            {
                details = await PreserveRemoteDeletionConflictAsync(
                        syncPair,
                        options,
                        relativePath,
                        local,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (remoteFile is not null)
            {
                details = await PreserveLocalDeletionConflictAsync(
                        syncPair,
                        options,
                        relativePath,
                        remoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            SyncActivityReporter.ReportActivity(result, options, SyncActivityKind.Conflict, relativePath, details);
        }

        private async Task<string> PreserveDivergedConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            await contentHashResolver.EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
            string conflictPath = localWriter.CreateConflictRelativePath(
                syncPair.LocalRootPath,
                relativePath,
                DateTime.UtcNow);
            EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, conflictPath, remoteFile.SizeBytes);
            await fileTransfer.WriteMaterializedRemoteFileAsync(
                    syncPair,
                    options,
                    conflictPath,
                    relativePath,
                    remoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            await stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, remoteFile),
                    cancellationToken)
                .ConfigureAwait(false);
            return "Remote version saved as " + conflictPath;
        }

        private async Task<string> PreserveRemoteDeletionConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            LocalFileSnapshot local,
            CancellationToken cancellationToken)
        {
            NodeFileManifestDto uploaded = await fileTransfer.UploadFileWithProgressAsync(
                    syncPair.RemoteRootNodeId,
                    relativePath,
                    local,
                    null,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            string localContentHash = ResolveUploadedLocalContentHash(local, uploaded);
            local.ContentHash = localContentHash;
            await stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, localContentHash, local.LastWriteUtc, local.SizeBytes, uploaded),
                    cancellationToken)
                .ConfigureAwait(false);
            return "Remote deletion conflicted with local change; local version was uploaded again.";
        }

        private async Task<string> PreserveLocalDeletionConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, relativePath, remoteFile.SizeBytes);
            await fileTransfer.WriteRemoteFileAfterLocalDeletionAsync(
                    syncPair,
                    options,
                    relativePath,
                    relativePath,
                    remoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            await stateStore.UpsertAsync(
                    BuildBaseline(
                        syncPair,
                        relativePath,
                        remoteFile.ContentHash,
                        remoteFile.UpdatedAt,
                        remoteFile.SizeBytes,
                        remoteFile),
                    cancellationToken)
                .ConfigureAwait(false);
            return "Local deletion conflicted with remote change; remote version was restored locally.";
        }
    }
}
