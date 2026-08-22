// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Converts durable remote change-feed items into bounded sync requests.
    /// </summary>
    internal class RemoteChangeScopedSyncPlanner
    {
        private readonly ISyncStateStore _stateStore;

        public RemoteChangeScopedSyncPlanner(ISyncStateStore stateStore)
        {
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        }

        public async Task<RemoteChangeScopedSyncPlan> CreatePlanAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            RemoteChangeFeedSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.IsEmpty)
            {
                return new RemoteChangeScopedSyncPlan(
                    CreatePlanWithoutMappedRemoteChanges(request, hasUnresolvedChanges: false),
                    HasUnresolvedChanges: false);
            }

            RemoteChangeStateIndex stateIndex =
                await LoadStateIndexAsync(syncPair, snapshot, cancellationToken).ConfigureAwait(false);
            HashSet<string> remoteChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> expandedSubtreePathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasUnresolvedChanges = false;
            foreach (RemoteChangeImpact change in snapshot.Changes)
            {
                HashSet<string> changePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                RemoteChangePathDisposition disposition = RemoteChangePathResolver.Resolve(
                    syncPair,
                    stateIndex,
                    change,
                    changePaths);
                if (disposition == RemoteChangePathDisposition.Mapped)
                {
                    remoteChangedPaths.UnionWith(changePaths);
                    await AddTrackedFolderSubtreePathsAsync(
                            syncPair,
                            change,
                            changePaths,
                            remoteChangedPaths,
                            expandedSubtreePathKeys,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (disposition == RemoteChangePathDisposition.Unresolved)
                {
                    hasUnresolvedChanges = true;
                }

                RemoteChangePathResolver.TryUpdateBatchFolderPath(syncPair, stateIndex, change);
            }

            SyncRunRequest? plannedRequest = null;
            if (remoteChangedPaths.Count > 0)
            {
                SyncRunRequest remoteRequest = SyncRunRequest.ForLocalChangedPaths(
                    remoteChangedPaths,
                    request.Causes | SyncRunCause.RealtimeRemoteChange);
                plannedRequest = request.IsFull
                    ? CreateFullRequestPlan(request, remoteRequest)
                    : request.Merge(remoteRequest);
            }
            else
            {
                plannedRequest = CreatePlanWithoutMappedRemoteChanges(request, hasUnresolvedChanges);
            }

            return new RemoteChangeScopedSyncPlan(plannedRequest, hasUnresolvedChanges);
        }

        internal static SyncRunRequest? CreatePlanWithoutMappedRemoteChanges(
            SyncRunRequest request,
            bool hasUnresolvedChanges)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!request.IsFull)
            {
                return request;
            }

            if (request.LocalChangedPaths.Count > 0 && CanScopeFullRequest(request.Causes))
            {
                return SyncRunRequest.ForLocalChangedPaths(
                    request.LocalChangedPaths,
                    request.LocalDeletedPaths,
                    request.Causes);
            }

            return !hasUnresolvedChanges && !CanSkipFullRequestWithoutMappedRemoteChanges(request.Causes)
                ? request
                : null;
        }

        internal static bool CanSkipFullRequestWithoutMappedRemoteChanges(SyncRunCause causes)
        {
            const SyncRunCause safeCauses = SyncRunCause.Periodic
                | SyncRunCause.RealtimeRemoteChange
                | SyncRunCause.Resume;
            return (causes & ~safeCauses) == SyncRunCause.None;
        }

        private static SyncRunRequest CreateFullRequestPlan(SyncRunRequest request, SyncRunRequest remoteRequest)
        {
            if (!CanScopeFullRequest(request.Causes))
            {
                return request.Merge(remoteRequest);
            }

            return SyncRunRequest.ForLocalChangedPaths(
                request.LocalChangedPaths.Concat(remoteRequest.LocalChangedPaths),
                request.LocalDeletedPaths.Concat(remoteRequest.LocalDeletedPaths),
                request.Causes | remoteRequest.Causes);
        }

        private static bool CanScopeFullRequest(SyncRunCause causes)
        {
            const SyncRunCause scopeEligibleFullCauses = SyncRunCause.Periodic
                | SyncRunCause.RealtimeRemoteChange
                | SyncRunCause.Resume;
            SyncRunCause fullCauses = causes & ~SyncRunCause.LocalChange;
            return fullCauses != SyncRunCause.None
                && (fullCauses & ~scopeEligibleFullCauses) == SyncRunCause.None;
        }

        private async Task AddTrackedFolderSubtreePathsAsync(
            SyncPairSettings syncPair,
            RemoteChangeImpact change,
            IEnumerable<string> candidatePaths,
            HashSet<string> paths,
            HashSet<string> expandedSubtreePathKeys,
            CancellationToken cancellationToken)
        {
            if (!ShouldExpandTrackedFolderSubtree(change))
            {
                return;
            }

            foreach (string candidatePath in candidatePaths.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNormalizeSyncPath(candidatePath, out string normalizedPath))
                {
                    continue;
                }

                string prefixKey = SyncPath.ToKey(normalizedPath);
                if (!expandedSubtreePathKeys.Add(prefixKey))
                {
                    continue;
                }

                await foreach (SyncStateEntry entry in _stateStore
                                   .LoadEntriesByPathPrefixAsync(
                                       syncPair.Id.ToString("D"),
                                       normalizedPath,
                                       cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                    {
                        paths.Add(entry.RelativePath);
                    }
                }
            }
        }

        private async Task<RemoteChangeStateIndex> LoadStateIndexAsync(
            SyncPairSettings syncPair,
            RemoteChangeFeedSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            HashSet<Guid> nodeIds = new HashSet<Guid>(snapshot.AffectedNodeIds.Where(static id => id != Guid.Empty))
            {
                syncPair.RemoteRootNodeId,
            };
            Guid[] fileIds = snapshot.AffectedNodeFileIds
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            RemoteChangeStateIndex index = new RemoteChangeStateIndex(syncPair.RemoteRootNodeId);
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadEntriesByRemoteIdsAsync(
                                   syncPair.Id.ToString("D"),
                                   nodeIds,
                                   fileIds,
                                   cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index.Add(entry);
            }

            return index;
        }

        private static bool ShouldExpandTrackedFolderSubtree(RemoteChangeImpact change)
        {
            return change.TargetKind == RemoteChangeTargetKind.Folder
                && change.Action is RemoteChangeAction.Deleted or RemoteChangeAction.Moved or RemoteChangeAction.Renamed;
        }

        private static bool TryNormalizeSyncPath(string relativePath, out string normalizedPath)
        {
            try
            {
                normalizedPath = SyncPath.Normalize(relativePath);
                return !string.IsNullOrWhiteSpace(normalizedPath);
            }
            catch (ArgumentException)
            {
                normalizedPath = string.Empty;
                return false;
            }
        }

    }
}
