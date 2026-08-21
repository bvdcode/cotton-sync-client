// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.LocalUploadPolicy;
using static Cotton.Sync.RemoteSyncErrorClassifier;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal class SyncFileUploadExecutor(
        SyncLocalContentHashResolver contentHashResolver,
        SyncRemoteFileTransfer fileTransfer,
        SyncFileConflictResolver conflictResolver,
        ISyncStateStore stateStore,
        ILogger logger)
    {
        public async Task UploadAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto? existingRemoteFile,
            CancellationToken cancellationToken)
        {
            if (ShouldDefer(local, options, out TimeSpan remainingQuietTime))
            {
                ReportDeferred(result, options, relativePath, remainingQuietTime);
                return;
            }

            await contentHashResolver.EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
            NodeFileManifestDto? uploaded = await TryUploadWithConflictHandlingAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local,
                    existingRemoteFile,
                    cancellationToken)
                .ConfigureAwait(false);
            if (uploaded is null)
            {
                return;
            }

            string localContentHash = ResolveUploadedLocalContentHash(local, uploaded);
            local.ContentHash = localContentHash;
            await stateStore.UpsertAsync(
                    BuildBaseline(syncPair, relativePath, localContentHash, local.LastWriteUtc, local.SizeBytes, uploaded),
                    cancellationToken)
                .ConfigureAwait(false);
            SyncActivityReporter.ReportActivity(result, options, SyncActivityKind.Uploaded, relativePath, null);
        }

        private async Task<NodeFileManifestDto?> TryUploadWithConflictHandlingAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto? existingRemoteFile,
            CancellationToken cancellationToken)
        {
            try
            {
                return await fileTransfer.UploadFileWithProgressAsync(
                    syncPair.RemoteRootNodeId,
                    relativePath,
                    local,
                    existingRemoteFile,
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (existingRemoteFile is not null && IsPreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await fileTransfer.FindLatestRemoteFileAsync(syncPair, relativePath, cancellationToken).ConfigureAwait(false);
                await conflictResolver.PreserveAsync(
                    syncPair,
                    options,
                    result,
                    relativePath,
                    local,
                        latestRemoteFile ?? existingRemoteFile,
                        cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (HttpRequestException exception) when (existingRemoteFile is null && IsConflict(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await fileTransfer.FindLatestRemoteFileAsync(
                        syncPair,
                        relativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (latestRemoteFile is null)
                {
                    throw;
                }

                return await ResolveRemoteCreateConflictAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        latestRemoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LocalFileUnavailableException exception)
            {
                ReportUnavailable(result, options, relativePath, exception);
                return null;
            }
        }

        private async Task<NodeFileManifestDto?> ResolveRemoteCreateConflictAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string relativePath,
            LocalFileSnapshot local,
            NodeFileManifestDto latestRemoteFile,
            CancellationToken cancellationToken)
        {
            bool contentMatches = ContentMatches(local.ContentHash, latestRemoteFile.ContentHash)
                && local.SizeBytes == latestRemoteFile.SizeBytes;
            if (!contentMatches)
            {
                await conflictResolver.PreserveAsync(
                        syncPair,
                        options,
                        result,
                        relativePath,
                        local,
                        latestRemoteFile,
                        cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            logger.LogInformation(
                "Remote file create for {RelativePath} hit conflict after matching content was committed; reusing file {RemoteFileId}.",
                relativePath,
                latestRemoteFile.Id);
            return latestRemoteFile;
        }
    }
}
