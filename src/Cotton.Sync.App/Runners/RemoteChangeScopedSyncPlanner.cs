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
                return new RemoteChangeScopedSyncPlan(request, HasUnresolvedChanges: false);
            }

            RemoteChangeStateIndex stateIndex =
                await LoadStateIndexAsync(syncPair, snapshot, cancellationToken).ConfigureAwait(false);
            var remoteChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expandedSubtreePathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasUnresolvedChanges = false;
            foreach (RemoteChangeImpact change in snapshot.Changes)
            {
                var changePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                RemoteChangePathDisposition disposition = ResolveChangePaths(syncPair, stateIndex, change, changePaths);
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
            }

            SyncRunRequest? plannedRequest = null;
            if (remoteChangedPaths.Count > 0)
            {
                SyncRunRequest remoteRequest = SyncRunRequest.ForLocalChangedPaths(
                    remoteChangedPaths,
                    request.Causes | SyncRunCause.RealtimeRemoteChange);
                plannedRequest = request.IsFull ? remoteRequest : request.Merge(remoteRequest);
            }
            else if (!request.IsFull)
            {
                plannedRequest = request;
            }

            return new RemoteChangeScopedSyncPlan(plannedRequest, hasUnresolvedChanges);
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
            var nodeIds = new HashSet<Guid>(snapshot.AffectedNodeIds.Where(static id => id != Guid.Empty))
            {
                syncPair.RemoteRootNodeId,
            };
            Guid[] fileIds = snapshot.AffectedNodeFileIds
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            var index = new RemoteChangeStateIndex(syncPair.RemoteRootNodeId);
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

            AddCreatedFolderPaths(syncPair, snapshot.Changes, index);
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

        private static void AddCreatedFolderPaths(
            SyncPairSettings syncPair,
            IReadOnlyList<RemoteChangeImpact> changes,
            RemoteChangeStateIndex stateIndex)
        {
            bool added;
            do
            {
                added = false;
                foreach (RemoteChangeImpact change in changes)
                {
                    if (!TryAddCreatedFolderPath(syncPair, stateIndex, change))
                    {
                        continue;
                    }

                    added = true;
                }
            }
            while (added);
        }

        private static bool TryAddCreatedFolderPath(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change)
        {
            if (change.TargetKind != RemoteChangeTargetKind.Folder
                || change.Action is not (RemoteChangeAction.Created or RemoteChangeAction.Restored)
                || !change.NodeId.HasValue
                || change.NodeId.Value == syncPair.RemoteRootNodeId
                || !change.ParentNodeId.HasValue
                || stateIndex.TryGetNodePath(change.NodeId.Value, out _)
                || ResolveNamedPath(stateIndex, change.ParentNodeId, change.Name, out string? relativePath)
                    != RemoteNamedPathStatus.Resolved
                || relativePath is null)
            {
                return false;
            }

            stateIndex.AddDirectory(change.NodeId.Value, relativePath);
            return true;
        }

        private static RemoteChangePathDisposition ResolveChangePaths(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            return change.TargetKind switch
            {
                RemoteChangeTargetKind.File => ResolveFileChangePaths(stateIndex, change, paths),
                RemoteChangeTargetKind.Folder => ResolveFolderChangePaths(syncPair, stateIndex, change, paths),
                _ => RemoteChangePathDisposition.Unresolved,
            };
        }

        private static RemoteChangePathDisposition ResolveFileChangePaths(
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            bool hasExistingPath = TryGetExistingFilePath(stateIndex, change, out string? existingPath);
            RemoteNamedPathStatus currentStatus = ResolveNamedPath(
                stateIndex,
                change.ParentNodeId,
                change.Name,
                out string? currentPath);
            RemoteNamedPathStatus previousStatus = change.Action is RemoteChangeAction.Moved or RemoteChangeAction.Deleted
                ? ResolveNamedPath(stateIndex, change.PreviousParentNodeId, change.Name, out string? previousPath)
                : ResolveWithoutPath(out previousPath);

            return ResolveActionPaths(
                change.Action,
                hasExistingPath,
                existingPath,
                currentStatus,
                currentPath,
                previousStatus,
                previousPath,
                paths);
        }

        private static RemoteChangePathDisposition ResolveFolderChangePaths(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            bool hasExistingPath = TryGetExistingFolderPath(syncPair, stateIndex, change, out string? existingPath);
            RemoteNamedPathStatus currentStatus = ResolveNamedPath(
                stateIndex,
                change.ParentNodeId,
                change.Name,
                out string? currentPath);
            RemoteNamedPathStatus previousStatus = change.Action is RemoteChangeAction.Moved or RemoteChangeAction.Deleted
                ? ResolveNamedPath(stateIndex, change.PreviousParentNodeId, change.Name, out string? previousPath)
                : ResolveWithoutPath(out previousPath);
            bool isRemoteRoot = change.NodeId == syncPair.RemoteRootNodeId;

            return ResolveActionPaths(
                change.Action,
                hasExistingPath,
                existingPath,
                currentStatus,
                currentPath,
                previousStatus,
                previousPath,
                paths,
                hasAdditionalRelation: isRemoteRoot);
        }

        private static RemoteChangePathDisposition ResolveActionPaths(
            RemoteChangeAction action,
            bool hasExistingPath,
            string? existingPath,
            RemoteNamedPathStatus currentStatus,
            string? currentPath,
            RemoteNamedPathStatus previousStatus,
            string? previousPath,
            HashSet<string> paths,
            bool hasAdditionalRelation = false)
        {
            bool currentResolved = currentStatus == RemoteNamedPathStatus.Resolved && currentPath is not null;
            bool previousResolved = previousStatus == RemoteNamedPathStatus.Resolved && previousPath is not null;
            bool hasRelation = hasAdditionalRelation
                || hasExistingPath
                || currentStatus != RemoteNamedPathStatus.UnknownParent
                || previousStatus != RemoteNamedPathStatus.UnknownParent;
            bool hasIgnoredPath = currentStatus == RemoteNamedPathStatus.Ignored
                || previousStatus == RemoteNamedPathStatus.Ignored;

            if (action is RemoteChangeAction.Created or RemoteChangeAction.Restored)
            {
                if (currentResolved)
                {
                    paths.Add(currentPath!);
                    return RemoteChangePathDisposition.Mapped;
                }

                return hasIgnoredPath
                    ? RemoteChangePathDisposition.Ignored
                    : hasRelation
                        ? RemoteChangePathDisposition.Unresolved
                        : RemoteChangePathDisposition.OutsidePair;
            }

            if (action == RemoteChangeAction.Renamed)
            {
                if (currentResolved)
                {
                    AddPathIfPresent(paths, existingPath);
                    paths.Add(currentPath!);
                    return RemoteChangePathDisposition.Mapped;
                }

                if (currentStatus == RemoteNamedPathStatus.Ignored)
                {
                    if (hasExistingPath)
                    {
                        paths.Add(existingPath!);
                        return RemoteChangePathDisposition.Mapped;
                    }

                    return RemoteChangePathDisposition.Ignored;
                }

                return hasRelation
                    ? RemoteChangePathDisposition.Unresolved
                    : RemoteChangePathDisposition.OutsidePair;
            }

            if (hasExistingPath)
            {
                paths.Add(existingPath!);
            }

            if (currentResolved)
            {
                paths.Add(currentPath!);
            }

            if (previousResolved)
            {
                paths.Add(previousPath!);
            }

            if (paths.Count > 0)
            {
                return RemoteChangePathDisposition.Mapped;
            }

            return hasIgnoredPath
                ? RemoteChangePathDisposition.Ignored
                : hasRelation
                    ? RemoteChangePathDisposition.Unresolved
                    : RemoteChangePathDisposition.OutsidePair;
        }

        private static bool TryGetExistingFilePath(
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            out string? existingPath)
        {
            existingPath = null;
            if (!change.NodeFileId.HasValue
                || !stateIndex.TryGetFilePath(change.NodeFileId.Value, out existingPath)
                || existingPath is null)
            {
                return false;
            }

            return true;
        }

        private static bool TryGetExistingFolderPath(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            out string? existingPath)
        {
            existingPath = null;
            if (!change.NodeId.HasValue)
            {
                return false;
            }

            if (change.NodeId.Value == syncPair.RemoteRootNodeId)
            {
                return false;
            }

            if (!stateIndex.TryGetNodePath(change.NodeId.Value, out existingPath)
                || existingPath is null)
            {
                return false;
            }

            return true;
        }

        private static RemoteNamedPathStatus ResolveNamedPath(
            RemoteChangeStateIndex stateIndex,
            Guid? parentNodeId,
            string name,
            out string? relativePath)
        {
            relativePath = null;
            if (!parentNodeId.HasValue
                || !stateIndex.TryGetNodePath(parentNodeId.Value, out string? parentPath)
                || parentPath is null)
            {
                return RemoteNamedPathStatus.UnknownParent;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return RemoteNamedPathStatus.Invalid;
            }

            string combined = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
            try
            {
                relativePath = SyncPath.Normalize(combined);
            }
            catch (ArgumentException)
            {
                relativePath = null;
                return RemoteNamedPathStatus.Invalid;
            }

            if (SyncPathIgnoreRules.ShouldIgnore(relativePath))
            {
                relativePath = null;
                return RemoteNamedPathStatus.Ignored;
            }

            return RemoteNamedPathStatus.Resolved;
        }

        private static RemoteNamedPathStatus ResolveWithoutPath(out string? relativePath)
        {
            relativePath = null;
            return RemoteNamedPathStatus.UnknownParent;
        }

        private static void AddPathIfPresent(HashSet<string> paths, string? path)
        {
            if (path is not null)
            {
                paths.Add(path);
            }
        }

        private class RemoteChangeStateIndex
        {
            private readonly Dictionary<Guid, string> _nodePathById = new();
            private readonly Dictionary<Guid, string> _filePathById = new();

            public RemoteChangeStateIndex(Guid remoteRootNodeId)
            {
                if (remoteRootNodeId != Guid.Empty)
                {
                    _nodePathById[remoteRootNodeId] = string.Empty;
                }
            }

            public void Add(SyncStateEntry entry)
            {
                ArgumentNullException.ThrowIfNull(entry);
                if (entry.Kind == SyncEntryKind.Directory && entry.RemoteNodeId.HasValue)
                {
                    AddDirectory(entry.RemoteNodeId.Value, entry.RelativePath);
                    return;
                }

                if (entry.Kind == SyncEntryKind.File && entry.RemoteFileId.HasValue)
                {
                    _filePathById[entry.RemoteFileId.Value] = SyncPath.Normalize(entry.RelativePath);
                }
            }

            public void AddDirectory(Guid nodeId, string relativePath)
            {
                _nodePathById[nodeId] = SyncPath.Normalize(relativePath);
            }

            public bool TryGetNodePath(Guid nodeId, out string? relativePath)
            {
                return _nodePathById.TryGetValue(nodeId, out relativePath);
            }

            public bool TryGetFilePath(Guid fileId, out string? relativePath)
            {
                return _filePathById.TryGetValue(fileId, out relativePath);
            }
        }
    }
}
