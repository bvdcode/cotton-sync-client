// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncTransferPlanner;

namespace Cotton.Sync
{
    internal class SyncFileMaterializer(
        IRemoteFilePlaceholderWriter? placeholderWriter,
        ISyncStateStore stateStore,
        ILocalFileSyncWriter localWriter,
        SyncRemoteFileTransfer fileTransfer)
    {
        public async Task DownloadAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken)
        {
            EnsureEnoughLocalFreeSpace(syncPair.LocalRootPath, relativePath, remoteFile.SizeBytes);
            await localWriter.WriteFileAsync(
                syncPair.LocalRootPath,
                relativePath,
                (stream, token) => fileTransfer.DownloadAndVerifyFileAsync(remoteFile, relativePath, options, stream, token),
                remoteFile.UpdatedAt == default ? null : remoteFile.UpdatedAt,
                cancellationToken).ConfigureAwait(false);
            await stateStore.UpsertAsync(BuildBaseline(syncPair, relativePath, remoteFile.ContentHash, remoteFile.UpdatedAt, remoteFile.SizeBytes, remoteFile), cancellationToken)
                .ConfigureAwait(false);
            SyncActivityReporter.ReportActivity(result, options, SyncActivityKind.Downloaded, relativePath, null);
        }

        public async Task<SyncStateEntry?> CreatePlaceholderStateAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                throw new InvalidOperationException("Initial virtual-files placeholder creation requires Windows virtual-files materialization.");
            }

            if (placeholderWriter is null)
            {
                throw new RemoteFilePlaceholderUnavailableException(
                    relativePath,
                    "Windows virtual-files placeholder writer is not available.");
            }

            RemoteFilePlaceholderResult placeholder;
            try
            {
                placeholder = await placeholderWriter
                    .CreatePlaceholderAsync(
                        RemoteFilePlaceholderRequestFactory.Create(syncPair, relativePath, remoteFile),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RemoteFilePlaceholderUnavailableException)
            {
                throw;
            }

            return BuildPlaceholderBaseline(syncPair, relativePath, remoteFile, placeholder, existingHydrationState);
        }

        public async Task MaterializeAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            NodeFileManifestDto remoteFile,
            CancellationToken cancellationToken,
            SyncPlaceholderHydrationState? existingHydrationState = null)
        {
            if (syncPair.MaterializationMode != SyncPairMaterializationMode.WindowsVirtualFiles)
            {
                await DownloadAsync(syncPair, options, result, relativePath, remoteFile, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            SyncStateEntry? placeholderState;
            try
            {
                placeholderState = await CreatePlaceholderStateAsync(
                        syncPair,
                        options,
                        relativePath,
                        remoteFile,
                        cancellationToken,
                        existingHydrationState)
                    .ConfigureAwait(false);
            }
            catch (RemoteFilePlaceholderUnavailableException exception)
            {
                SyncActivityReporter.ReportActivity(
                    result,
                    options,
                    SyncActivityKind.Skipped,
                    relativePath,
                    exception.Reason,
                    requiresUserAction: true);
                return;
            }

            if (placeholderState is not null)
            {
                await stateStore.UpsertAsync(placeholderState, cancellationToken).ConfigureAwait(false);
                SyncActivityReporter.ReportActivity(result, options, SyncActivityKind.PlaceholderCreated, relativePath, null);
            }
        }
    }
}
