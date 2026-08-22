// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.LocalUploadPolicy;
using static Cotton.Sync.RemoteSyncErrorClassifier;
using static Cotton.Sync.SyncBaselineFactory;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal class SyncLocalFileMoveCoordinator(
        SyncLocalContentHashResolver contentHashResolver,
        IRemoteFileSynchronizer remoteFiles,
        SyncRemoteFileTransfer fileTransfer,
        ISyncStateStore stateStore)
    {
        public async Task CoalesceAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, SyncStateEntry>> moveSources = FindLocalMoveSources(localByPath, remoteByPath, stateByPath);
            if (moveSources.Count == 0)
            {
                return;
            }

            Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>> candidates =
                await BuildLocalMoveCandidateBucketsAsync(
                        localByPath,
                        remoteByPath,
                        stateByPath,
                        options,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, SyncStateEntry> source in moveSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!remoteByPath.TryGetValue(source.Key, out RemoteFileSnapshot? remote)
                    || string.IsNullOrWhiteSpace(source.Value.LocalContentHash)
                    || !source.Value.LocalSizeBytes.HasValue)
                {
                    continue;
                }

                MoveCandidateKey candidateKey = new MoveCandidateKey(source.Value.LocalContentHash, source.Value.LocalSizeBytes.Value);
                if (!candidates.TryGetValue(candidateKey, out Queue<LocalFileSnapshot>? bucket)
                    || !TryDequeueCurrentCandidate(bucket, remoteByPath, stateByPath, out LocalFileSnapshot? local))
                {
                    continue;
                }

                await MoveRemoteFileAsync(
                    syncPair,
                    options,
                    result,
                    source.Key,
                    source.Value,
                    local,
                    remote,
                    remoteByPath,
                    stateByPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static List<KeyValuePair<string, SyncStateEntry>> FindLocalMoveSources(
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath)
        {
            List<KeyValuePair<string, SyncStateEntry>> result = new List<KeyValuePair<string, SyncStateEntry>>();
            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                if (state.Value.Kind != SyncEntryKind.File
                    || string.IsNullOrWhiteSpace(state.Value.LocalContentHash)
                    || !state.Value.LocalSizeBytes.HasValue
                    || localByPath.ContainsKey(state.Key)
                    || !remoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote)
                    || !RemoteMatchesBaseline(remote.File, state.Value))
                {
                    continue;
                }

                result.Add(state);
            }

            return result;
        }

        private async Task<Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>>> BuildLocalMoveCandidateBucketsAsync(
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            SyncRunOptions options,
            SyncRunResult result,
            CancellationToken cancellationToken)
        {
            Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>> candidates = new Dictionary<MoveCandidateKey, Queue<LocalFileSnapshot>>();
            foreach (KeyValuePair<string, LocalFileSnapshot> local in localByPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stateByPath.ContainsKey(local.Key) || remoteByPath.ContainsKey(local.Key))
                {
                    continue;
                }

                if (local.Value.IsCloudFilesOnlineOnlyPlaceholder)
                {
                    continue;
                }

                if (result.IsLocalPathDeferred(local.Value.RelativePath))
                {
                    continue;
                }

                try
                {
                    await contentHashResolver.EnsureAsync(local.Value, options, cancellationToken).ConfigureAwait(false);
                }
                catch (LocalFileUnavailableException exception)
                {
                    ReportUnavailable(result, options, local.Value.RelativePath, exception);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(local.Value.ContentHash))
                {
                    continue;
                }

                MoveCandidateKey candidateKey = new MoveCandidateKey(local.Value.ContentHash, local.Value.SizeBytes);
                if (!candidates.TryGetValue(candidateKey, out Queue<LocalFileSnapshot>? bucket))
                {
                    bucket = new Queue<LocalFileSnapshot>();
                    candidates[candidateKey] = bucket;
                }

                bucket.Enqueue(local.Value);
            }

            return candidates;
        }

        private static bool TryDequeueCurrentCandidate(
            Queue<LocalFileSnapshot> bucket,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            out LocalFileSnapshot local)
        {
            while (bucket.Count > 0)
            {
                LocalFileSnapshot candidate = bucket.Dequeue();
                string key = SyncPath.ToKey(candidate.RelativePath);
                if (!remoteByPath.ContainsKey(key) && !stateByPath.ContainsKey(key))
                {
                    local = candidate;
                    return true;
                }
            }

            local = null!;
            return false;
        }

        private async Task MoveRemoteFileAsync(
            SyncPair syncPair,
            SyncRunOptions options,
            SyncRunResult result,
            string sourceKey,
            SyncStateEntry sourceState,
            LocalFileSnapshot local,
            RemoteFileSnapshot remote,
            IDictionary<string, RemoteFileSnapshot> remoteByPath,
            IDictionary<string, SyncStateEntry> stateByPath,
            CancellationToken cancellationToken)
        {
            string sourcePath = sourceState.RelativePath;
            string targetPath = local.RelativePath;
            NodeFileManifestDto moved;
            try
            {
                moved = await remoteFiles
                    .MoveFileAsync(syncPair.RemoteRootNodeId, targetPath, remote.File, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (IsPreconditionFailed(exception))
            {
                NodeFileManifestDto? latestRemoteFile = await fileTransfer.FindLatestRemoteFileAsync(syncPair, sourcePath, cancellationToken).ConfigureAwait(false);
                if (latestRemoteFile is null)
                {
                    remoteByPath.Remove(sourceKey);
                }
                else
                {
                    remoteByPath[sourceKey] = new RemoteFileSnapshot
                    {
                        RelativePath = sourcePath,
                        File = latestRemoteFile,
                    };
                }

                return;
            }

            string targetKey = SyncPath.ToKey(targetPath);
            remoteByPath.Remove(sourceKey);
            remoteByPath[targetKey] = new RemoteFileSnapshot
            {
                RelativePath = targetPath,
                File = moved,
            };
            stateByPath.Remove(sourceKey);
            SyncStateEntry targetState = BuildBaseline(syncPair, targetPath, local.ContentHash, local.LastWriteUtc, local.SizeBytes, moved);
            stateByPath[targetKey] = targetState;
            await stateStore.DeleteAsync(syncPair.SyncPairId, sourcePath, cancellationToken).ConfigureAwait(false);
            await stateStore.UpsertAsync(targetState, cancellationToken).ConfigureAwait(false);
            SyncActivityReporter.ReportActivity(result, options, SyncActivityKind.Moved, targetPath, "Moved from " + sourcePath + ".");
        }
    }
}
