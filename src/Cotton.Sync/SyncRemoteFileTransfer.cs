// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync
{
    internal class SyncRemoteFileTransfer(
        ILocalFileSyncWriter localWriter,
        IRemoteFileSynchronizer remoteFiles,
        IRemoteFileMaterializationObserver? materializationObserver,
        IRemotePathLookupCrawler? remotePathLookupCrawler)
    {
        public async Task WriteMaterializedRemoteFileAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetRelativePath,
            string remoteRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            RemoteFileMaterializationRequest? request = await PrepareRemoteFileMaterializationAsync(
                syncPair,
                targetRelativePath,
                remoteFile,
                cancellationToken).ConfigureAwait(false);

            await WriteRemoteFileContentAsync(
                    syncPair,
                    options,
                    targetRelativePath,
                    remoteRelativePath,
                    remoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request is not null)
            {
                await materializationObserver!.AfterWriteFileAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public async Task WriteRemoteFileAfterLocalDeletionAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetRelativePath,
            string remoteRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            await PrepareRemoteFileMaterializationAsync(
                syncPair,
                targetRelativePath,
                remoteFile,
                cancellationToken).ConfigureAwait(false);

            await WriteRemoteFileContentAsync(
                    syncPair,
                    options,
                    targetRelativePath,
                    remoteRelativePath,
                    remoteFile,
                cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<RemoteFileMaterializationRequest?> PrepareRemoteFileMaterializationAsync(
            SyncPair syncPair,
            string targetRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            RemoteFileMaterializationRequest? request = CreateRemoteFileMaterializationRequest(
                syncPair,
                targetRelativePath,
                remoteFile);
            if (request is not null)
            {
                await materializationObserver!.BeforeWriteFileAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }

            return request;
        }

        private async Task WriteRemoteFileContentAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetRelativePath,
            string remoteRelativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            await localWriter.WriteFileAsync(
                    syncPair.LocalRootPath,
                    targetRelativePath,
                    (stream, token) => DownloadAndVerifyFileAsync(remoteFile, remoteRelativePath, options, stream, token),
                    remoteFile.UpdatedAt == default ? null : remoteFile.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private RemoteFileMaterializationRequest? CreateRemoteFileMaterializationRequest(
            SyncPair syncPair,
            string relativePath,
            NodeFileManifestDto remoteFile)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles
                || materializationObserver is null)
            {
                return null;
            }

            return new RemoteFileMaterializationRequest(
                syncPair.SyncPairId,
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                relativePath,
                remoteFile);
        }

        public async Task<NodeFileManifestDto> UploadFileWithProgressAsync(
            Guid rootNodeId,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto? existingRemoteFile,
            SyncRunOptions options,
            CancellationToken cancellationToken)
        {
            if (remoteFiles is IRemoteFileTransferProgressSynchronizer progressSynchronizer)
            {
                return await progressSynchronizer.UploadFileAsync(
                    rootNodeId,
                    relativePath,
                    local,
                    existingRemoteFile,
                    options.TransferProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            SyncActivityReporter.ReportTransfer(
                options,
                SyncTransferDirection.Upload,
                relativePath,
                transferredBytes: 0,
                totalBytes: local.SizeBytes);
            NodeFileManifestDto uploaded = await remoteFiles.UploadFileAsync(
                rootNodeId,
                relativePath,
                local,
                existingRemoteFile,
                cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.ReportTransfer(
                options,
                SyncTransferDirection.Upload,
                relativePath,
                local.SizeBytes,
                local.SizeBytes,
                isCompleted: true);
            return uploaded;
        }

        private async Task DownloadFileWithProgressAsync(
            NodeFileManifestDto remoteFile,
            string relativePath,
            SyncRunOptions options,
            Stream destination,
            CancellationToken cancellationToken)
        {
            if (remoteFiles is IRemoteFileTransferProgressSynchronizer progressSynchronizer)
            {
                await progressSynchronizer.DownloadFileAsync(
                    remoteFile.Id,
                    relativePath,
                    remoteFile.SizeBytes,
                    destination,
                    options.TransferProgress,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            SyncActivityReporter.ReportTransfer(
                options,
                SyncTransferDirection.Download,
                relativePath,
                transferredBytes: 0,
                totalBytes: remoteFile.SizeBytes);
            await remoteFiles.DownloadFileAsync(remoteFile.Id, destination, cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.ReportTransfer(
                options,
                SyncTransferDirection.Download,
                relativePath,
                remoteFile.SizeBytes,
                remoteFile.SizeBytes,
                isCompleted: true);
        }

        public async Task DownloadAndVerifyFileAsync(
            NodeFileManifestDto remoteFile,
            string relativePath,
            SyncRunOptions options,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await using VerifyingDownloadStream verifiedDestination = new VerifyingDownloadStream(destination);
            await DownloadFileWithProgressAsync(remoteFile, relativePath, options, verifiedDestination, cancellationToken)
                .ConfigureAwait(false);
            verifiedDestination.Verify(remoteFile.ContentHash, remoteFile.SizeBytes, relativePath);
        }

        public async Task<NodeFileManifestDto?> FindLatestRemoteFileAsync(
            SyncPair syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (remotePathLookupCrawler is null)
            {
                throw new InvalidOperationException("Remote mutation recovery requires path lookup capability.");
            }

            string normalizedPath = SyncPath.Normalize(relativePath);
            RemoteTreeLookupSnapshot latestTree = await remotePathLookupCrawler
                .CrawlPathLookupsAsync(
                    syncPair.RemoteRootNodeId,
                    [normalizedPath],
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            string key = SyncPath.ToKey(relativePath);
            return latestTree.FilesByPath.TryGetValue(key, out RemoteFileSnapshot? remoteFile)
                ? remoteFile.File
                : null;
        }
    }
}
