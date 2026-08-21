// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncFileStateEvaluator;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class RemoteDirectoryMoveCoordinator(
        RemoteDirectoryMovePlanner planner,
        ILocalFileSyncWriter localWriter,
        ISyncStateStore stateStore,
        IRemoteDirectoryTreePopulationObserver? directoryPopulationObserver,
        IRemoteFilePlaceholderWriter? placeholderWriter,
        SyncLocalContentHashResolver contentHashResolver)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public async Task CoalesceAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            if (directoryStateByPath.Count == 0)
            {
                return;
            }

            Dictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById =
                RemoteDirectoryMovePlanner.BuildUniqueRemoteDirectoriesById(
                remoteDirectoriesByPath.Values);
            Dictionary<Guid, RemoteFileSnapshot> remoteFilesById =
                RemoteDirectoryMovePlanner.BuildUniqueRemoteFilesById(remoteFilesByPath.Values);
            List<RemoteDirectoryMoveCandidate> accepted = planner.FindRemoteDirectoryMoveCandidates(
                localDirectoriesByPath,
                localFilesByPath,
                directoryStateByPath,
                fileStateByPath,
                remoteDirectoriesById,
                remoteFilesById,
                cancellationToken);
            foreach (RemoteDirectoryMoveCandidate candidate in accepted)
            {
                await ApplyRemoteDirectoryMoveAsync(
                    syncPair,
                    options,
                    result,
                    candidate,
                    localDirectoriesByPath,
                    localFilesByPath,
                    directoryStateByPath,
                    fileStateByPath,
                    remoteDirectoriesById,
                    remoteFilesById,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ApplyRemoteDirectoryMoveAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureRemoteDirectoryMoveLocalHashesAsync(
                syncPair,
                options,
                candidate,
                localFilesByPath,
                fileStateByPath,
                cancellationToken).ConfigureAwait(false);
            await localWriter.MoveDirectoryAsync(
                syncPair.LocalRootPath,
                candidate.SourcePath,
                candidate.TargetPath,
                cancellationToken).ConfigureAwait(false);
            RemoteDirectoryMovePlanner.MoveLocalDirectoryLookups(
                syncPair.LocalRootPath,
                candidate,
                localDirectoriesByPath);
            RemoteDirectoryMovePlanner.MoveLocalFileLookups(
                syncPair.LocalRootPath,
                candidate,
                localFilesByPath);

            List<KeyValuePair<string, SyncStateEntry>> movedDirectoryStates = directoryStateByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .OrderBy(entry => GetPathDepth(entry.Value.RelativePath))
                .ToList();
            await NotifyRemoteDirectoryMovePopulationAsync(
                syncPair,
                candidate,
                movedDirectoryStates,
                remoteDirectoriesById,
                cancellationToken).ConfigureAwait(false);
            await MoveRemoteDirectoryStatesAsync(
                syncPair,
                candidate,
                movedDirectoryStates,
                directoryStateByPath,
                remoteDirectoriesById,
                cancellationToken).ConfigureAwait(false);
            await MoveRemoteFileStatesAsync(
                syncPair,
                options,
                candidate,
                localFilesByPath,
                fileStateByPath,
                remoteFilesById,
                cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.Record(
                result,
                options,
                SyncActivityKind.Moved,
                candidate.TargetPath,
                "Moved local folder to follow the remote folder path.");
        }

        private async Task NotifyRemoteDirectoryMovePopulationAsync(
            SyncPair syncPair,
            RemoteDirectoryMoveCandidate candidate,
            IEnumerable<KeyValuePair<string, SyncStateEntry>> movedDirectoryStates,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            CancellationToken cancellationToken)
        {
            if (directoryPopulationObserver is null)
            {
                return;
            }

            List<RemoteDirectoryMaterializationRequest> directoryRequests = movedDirectoryStates
                .Select(entry =>
                {
                    RemoteDirectorySnapshot remote = remoteDirectoriesById[entry.Value.RemoteNodeId!.Value];
                    string targetPath = ReplacePathPrefix(
                        entry.Value.RelativePath,
                        candidate.SourcePath,
                        candidate.TargetPath);
                    return SyncDirectoryReconciler.CreateRemoteDirectoryMaterializationRequest(
                        syncPair,
                        targetPath,
                        remote.Node);
                })
                .ToList();
            await directoryPopulationObserver
                .AfterDirectoryTreePopulationAsync(directoryRequests, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task MoveRemoteDirectoryStatesAsync(
            SyncPair syncPair,
            RemoteDirectoryMoveCandidate candidate,
            IEnumerable<KeyValuePair<string, SyncStateEntry>> movedDirectoryStates,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in movedDirectoryStates)
            {
                string targetPath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                RemoteDirectorySnapshot remote = remoteDirectoriesById[entry.Value.RemoteNodeId!.Value];
                SyncStateEntry movedState = BuildDirectoryBaseline(syncPair, targetPath, remote.Node);
                await MoveStateEntryAsync(
                    syncPair.SyncPairId,
                    entry.Value.RelativePath,
                    movedState,
                    directoryStateByPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task MoveRemoteFileStatesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, SyncStateEntry>> movedFileStates = fileStateByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .OrderBy(entry => entry.Value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (KeyValuePair<string, SyncStateEntry> entry in movedFileStates)
            {
                string targetPath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                string targetKey = SyncPath.ToKey(targetPath);
                RemoteFileSnapshot remote = remoteFilesById[entry.Value.RemoteFileId!.Value];
                LocalFileSnapshot local = localFilesByPath[targetKey];
                SyncStateEntry movedState = await BuildMovedRemoteFileStateAsync(
                    syncPair,
                    options,
                    targetPath,
                    local,
                    remote.File,
                    entry.Value,
                    cancellationToken).ConfigureAwait(false);
                await MoveStateEntryAsync(
                    syncPair.SyncPairId,
                    entry.Value.RelativePath,
                    movedState,
                    fileStateByPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task EnsureRemoteDirectoryMoveLocalHashesAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in fileStateByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!localFilesByPath.TryGetValue(entry.Key, out LocalFileSnapshot? local)
                    || IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, entry.Value)
                    || string.IsNullOrWhiteSpace(entry.Value.LocalContentHash))
                {
                    continue;
                }

                await contentHashResolver.EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<SyncStateEntry> BuildMovedRemoteFileStateAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            string targetPath,
            LocalFileSnapshot local,
            NodeFileManifestDto remoteFile,
            SyncStateEntry previousState,
            CancellationToken cancellationToken)
        {
            bool localMatchesBaseline = IsLocalOnlineOnlyPlaceholderBaseline(syncPair, local, previousState);
            if (!localMatchesBaseline && !string.IsNullOrWhiteSpace(previousState.LocalContentHash))
            {
                await contentHashResolver.EnsureAsync(local, options, cancellationToken).ConfigureAwait(false);
                localMatchesBaseline = ContentMatches(local.ContentHash, previousState.LocalContentHash)
                    && (!previousState.LocalSizeBytes.HasValue || local.SizeBytes == previousState.LocalSizeBytes.Value);
            }

            if (syncPair.MaterializationMode == SyncPairMaterializationMode.WindowsVirtualFiles
                && local.IsCloudFilesPlaceholder
                && localMatchesBaseline
                && placeholderWriter is not null)
            {
                RemoteFilePlaceholderResult placeholder = await placeholderWriter
                    .CreatePlaceholderAsync(
                        RemoteFilePlaceholderRequestFactory.Create(
                            syncPair,
                            targetPath,
                            remoteFile,
                            previousState.PlaceholderHydrationState),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (placeholder.LocalSizeBytes.HasValue)
                {
                    local.SizeBytes = placeholder.LocalSizeBytes.Value;
                }

                if (placeholder.LocalLastWriteUtc.HasValue)
                {
                    local.LastWriteUtc = placeholder.LocalLastWriteUtc.Value.ToUniversalTime();
                }

                return BuildPlaceholderBaseline(
                    syncPair,
                    targetPath,
                    remoteFile,
                    placeholder,
                    previousState.PlaceholderHydrationState);
            }

            return new SyncStateEntry
            {
                SyncPairId = syncPair.SyncPairId,
                RelativePath = SyncPath.Normalize(targetPath),
                Kind = SyncEntryKind.File,
                LocalContentHash = previousState.LocalContentHash,
                LocalLastWriteUtc = previousState.LocalLastWriteUtc,
                LocalSizeBytes = previousState.LocalSizeBytes,
                RemoteSizeBytes = previousState.RemoteSizeBytes,
                RemoteNodeId = remoteFile.NodeId,
                RemoteFileId = previousState.RemoteFileId,
                RemoteFileManifestId = previousState.RemoteFileManifestId,
                RemoteOriginalNodeFileId = previousState.RemoteOriginalNodeFileId,
                RemoteContentHash = previousState.RemoteContentHash,
                RemoteETag = previousState.RemoteETag,
                PlaceholderIdentity = previousState.PlaceholderIdentity,
                PlaceholderHydrationState = previousState.PlaceholderHydrationState,
                SyncedAtUtc = DateTime.UtcNow,
            };
        }

        private async Task MoveStateEntryAsync(
            string syncPairId,
            string sourcePath,
            SyncStateEntry movedState,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            string sourceKey = SyncPath.ToKey(sourcePath);
            string targetKey = SyncPath.ToKey(movedState.RelativePath);
            await stateStore.UpsertAsync(movedState, cancellationToken).ConfigureAwait(false);
            if (!PathComparer.Equals(sourceKey, targetKey))
            {
                await stateStore.DeleteAsync(syncPairId, sourcePath, cancellationToken).ConfigureAwait(false);
                stateByPath.Remove(sourceKey);
            }

            stateByPath[targetKey] = movedState;
        }
    }
}
