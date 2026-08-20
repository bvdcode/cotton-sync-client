// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Remote;
using Cotton.Sync.State;

namespace Cotton.Sync.App.Runners
{
    internal static class RemoteChangePathResolver
    {
        public static RemoteChangePathDisposition Resolve(
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

        public static bool TryUpdateBatchFolderPath(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change)
        {
            if (change.TargetKind != RemoteChangeTargetKind.Folder
                || change.Action is not (RemoteChangeAction.Created
                    or RemoteChangeAction.Restored
                    or RemoteChangeAction.Moved
                    or RemoteChangeAction.Renamed)
                || !change.NodeId.HasValue
                || change.NodeId.Value == syncPair.RemoteRootNodeId
                || !change.ParentNodeId.HasValue
                || ResolveNamedPath(stateIndex, change.ParentNodeId, change.Name, out string? relativePath)
                    != RemoteNamedPathStatus.Resolved
                || relativePath is null)
            {
                return false;
            }

            stateIndex.AddDirectory(change.NodeId.Value, relativePath);
            return true;
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
            if (change.NodeId == syncPair.RemoteRootNodeId && change.Action == RemoteChangeAction.Created)
            {
                return RemoteChangePathDisposition.Ignored;
            }

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
            bool currentResolved = IsResolvedPath(currentStatus, currentPath);
            bool previousResolved = IsResolvedPath(previousStatus, previousPath);
            bool hasRelation = HasRemotePathRelation(
                hasAdditionalRelation,
                hasExistingPath,
                currentStatus,
                previousStatus);
            bool hasIgnoredPath = HasIgnoredRemotePath(currentStatus, previousStatus);

            switch (action)
            {
                case RemoteChangeAction.Created:
                case RemoteChangeAction.Restored:
                    return ResolveCreatedOrRestoredPath(
                        currentResolved,
                        currentPath,
                        hasIgnoredPath,
                        hasRelation,
                        paths);
                case RemoteChangeAction.Renamed:
                    return ResolveRenamedPath(
                        hasExistingPath,
                        existingPath,
                        currentResolved,
                        currentStatus,
                        currentPath,
                        hasRelation,
                        paths);
                case RemoteChangeAction.Unknown:
                case RemoteChangeAction.ContentUpdated:
                case RemoteChangeAction.Moved:
                case RemoteChangeAction.Deleted:
                    return ResolveRelatedPaths(
                        hasExistingPath,
                        existingPath,
                        currentResolved,
                        currentPath,
                        previousResolved,
                        previousPath,
                        hasIgnoredPath,
                        hasRelation,
                        paths);
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported remote change action.");
            }
        }

        private static bool IsResolvedPath(RemoteNamedPathStatus status, string? path)
        {
            return status == RemoteNamedPathStatus.Resolved && path is not null;
        }

        private static bool HasRemotePathRelation(
            bool hasAdditionalRelation,
            bool hasExistingPath,
            RemoteNamedPathStatus currentStatus,
            RemoteNamedPathStatus previousStatus)
        {
            return hasAdditionalRelation
                || hasExistingPath
                || currentStatus != RemoteNamedPathStatus.UnknownParent
                || previousStatus != RemoteNamedPathStatus.UnknownParent;
        }

        private static bool HasIgnoredRemotePath(
            RemoteNamedPathStatus currentStatus,
            RemoteNamedPathStatus previousStatus)
        {
            return currentStatus == RemoteNamedPathStatus.Ignored
                || previousStatus == RemoteNamedPathStatus.Ignored;
        }

        private static RemoteChangePathDisposition ResolveCreatedOrRestoredPath(
            bool currentResolved,
            string? currentPath,
            bool hasIgnoredPath,
            bool hasRelation,
            HashSet<string> paths)
        {
            if (currentResolved)
            {
                paths.Add(currentPath!);
                return RemoteChangePathDisposition.Mapped;
            }

            return ResolveUnmappedDisposition(hasIgnoredPath, hasRelation);
        }

        private static RemoteChangePathDisposition ResolveRenamedPath(
            bool hasExistingPath,
            string? existingPath,
            bool currentResolved,
            RemoteNamedPathStatus currentStatus,
            string? currentPath,
            bool hasRelation,
            HashSet<string> paths)
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

            return ResolveUnmappedDisposition(hasIgnoredPath: false, hasRelation: hasRelation);
        }

        private static RemoteChangePathDisposition ResolveRelatedPaths(
            bool hasExistingPath,
            string? existingPath,
            bool currentResolved,
            string? currentPath,
            bool previousResolved,
            string? previousPath,
            bool hasIgnoredPath,
            bool hasRelation,
            HashSet<string> paths)
        {
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

            return ResolveUnmappedDisposition(hasIgnoredPath, hasRelation);
        }

        private static RemoteChangePathDisposition ResolveUnmappedDisposition(
            bool hasIgnoredPath,
            bool hasRelation)
        {
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
            return change.NodeFileId.HasValue
                && stateIndex.TryGetFilePath(change.NodeFileId.Value, out existingPath)
                && existingPath is not null;
        }

        private static bool TryGetExistingFolderPath(
            SyncPairSettings syncPair,
            RemoteChangeStateIndex stateIndex,
            RemoteChangeImpact change,
            out string? existingPath)
        {
            existingPath = null;
            return change.NodeId.HasValue
                && change.NodeId.Value != syncPair.RemoteRootNodeId
                && stateIndex.TryGetNodePath(change.NodeId.Value, out existingPath)
                && existingPath is not null;
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
    }
}
