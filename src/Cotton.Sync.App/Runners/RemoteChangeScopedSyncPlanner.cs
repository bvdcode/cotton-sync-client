// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

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

        public async Task<SyncRunRequest?> TryCreateScopedRequestAsync(
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
                return request;
            }

            RemoteChangeStateIndex stateIndex =
                await LoadStateIndexAsync(syncPair, snapshot, cancellationToken).ConfigureAwait(false);
            var remoteChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RemoteChangeImpact change in snapshot.Changes)
            {
                if (!TryAddChangePaths(syncPair, stateIndex, change, remoteChangedPaths))
                {
                    return null;
                }
            }

            if (remoteChangedPaths.Count == 0)
            {
                return null;
            }

            SyncRunRequest remoteRequest = SyncRunRequest.ForLocalChangedPaths(remoteChangedPaths);
            return request.IsFull ? remoteRequest : request.Merge(remoteRequest);
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
                || !stateIndex.TryGetNodePath(change.ParentNodeId.Value, out string? parentPath)
                || parentPath is null
                || !TryCombinePath(parentPath, change.Name, out string? relativePath))
            {
                return false;
            }

            stateIndex.AddDirectory(change.NodeId.Value, relativePath);
            return true;
        }

        private static bool TryAddChangePaths(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            return change.TargetKind switch
            {
                RemoteChangeTargetKind.File => TryAddFileChangePaths(syncPair, stateIndex, change, paths),
                RemoteChangeTargetKind.Folder => TryAddFolderChangePaths(syncPair, stateIndex, change, paths),
                _ => false,
            };
        }

        private static bool TryAddFileChangePaths(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            bool hasOldPath = TryAddExistingFilePath(stateIndex, change, paths);
            if (change.Action == RemoteChangeAction.Deleted)
            {
                return hasOldPath || TryAddCurrentNamedPath(syncPair, stateIndex, change, paths);
            }

            if (!TryAddCurrentNamedPath(syncPair, stateIndex, change, paths))
            {
                return false;
            }

            return change.Action is RemoteChangeAction.Renamed or RemoteChangeAction.Moved
                ? hasOldPath
                : true;
        }

        private static bool TryAddFolderChangePaths(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            bool hasOldPath = TryAddExistingFolderPath(syncPair, stateIndex, change, paths);
            if (change.Action == RemoteChangeAction.Deleted)
            {
                return hasOldPath || TryAddCurrentNamedPath(syncPair, stateIndex, change, paths);
            }

            if (!TryAddCurrentNamedPath(syncPair, stateIndex, change, paths))
            {
                return false;
            }

            return change.Action is RemoteChangeAction.Renamed or RemoteChangeAction.Moved
                ? hasOldPath
                : true;
        }

        private static bool TryAddExistingFilePath(
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            if (!change.NodeFileId.HasValue
                || !stateIndex.TryGetFilePath(change.NodeFileId.Value, out string? existingPath)
                || existingPath is null)
            {
                return false;
            }

            paths.Add(existingPath);
            return true;
        }

        private static bool TryAddExistingFolderPath(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            if (!change.NodeId.HasValue)
            {
                return false;
            }

            if (change.NodeId.Value == syncPair.RemoteRootNodeId)
            {
                return false;
            }

            if (!stateIndex.TryGetNodePath(change.NodeId.Value, out string? existingPath)
                || existingPath is null)
            {
                return false;
            }

            paths.Add(existingPath);
            return true;
        }

        private static bool TryAddCurrentNamedPath(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            HashSet<string> paths)
        {
            if (!change.ParentNodeId.HasValue)
            {
                return false;
            }

            if (!stateIndex.TryGetNodePath(change.ParentNodeId.Value, out string? parentPath)
                || parentPath is null)
            {
                return false;
            }

            if (!TryCombinePath(parentPath, change.Name, out string? relativePath))
            {
                return false;
            }

            if (change.TargetKind == RemoteChangeTargetKind.Folder
                && change.NodeId == syncPair.RemoteRootNodeId)
            {
                return false;
            }

            paths.Add(relativePath);
            return true;
        }

        private static bool TryCombinePath(string parentPath, string name, out string relativePath)
        {
            relativePath = string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string combined = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
            try
            {
                relativePath = SyncPath.Normalize(combined);
                return !SyncPathIgnoreRules.ShouldIgnore(relativePath);
            }
            catch (ArgumentException)
            {
                return false;
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
