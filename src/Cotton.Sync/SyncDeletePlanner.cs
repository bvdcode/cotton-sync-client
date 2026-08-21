// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncFileStateEvaluator;

namespace Cotton.Sync
{
    internal static class SyncDeletePlanner
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static SyncDeleteGuard BuildDeleteGuard(
            SyncRunOptions options,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath,
            DirectoryContentIndex localDirectoryContentIndex,
            DirectoryContentIndex remoteDirectoryContentIndex,
            IReadOnlySet<string>? scopedFileDeleteKeys,
            IReadOnlySet<string>? scopedDirectoryDeleteKeys,
            IReadOnlySet<string> scopedLocalDeletedFileKeys,
            ScopedVirtualFilesDirectoryDeletePlan? scopedDirectoryDelete)
        {
            if (stateByPath.Count == 0 && directoryStateByPath.Count == 0)
            {
                return new SyncDeleteGuard(options, plannedLocalDeletes: 0, []);
            }

            (int LocalDeletes, IReadOnlyList<string> RemoteDeleteItems) fileDeletes = CountPlannedFileDeletes(
                stateByPath,
                localByPath,
                remoteByPath,
                scopedFileDeleteKeys,
                scopedLocalDeletedFileKeys);
            (int LocalDeletes, IReadOnlyList<string> RemoteDeleteItems) directoryDeletes = CountPlannedDirectoryDeletes(
                directoryStateByPath,
                localDirectoriesByPath,
                remoteDirectoriesByPath,
                localDirectoryContentIndex,
                remoteDirectoryContentIndex,
                scopedDirectoryDeleteKeys,
                scopedDirectoryDelete);
            List<string> remoteDeletePlanItems = [.. fileDeletes.RemoteDeleteItems, .. directoryDeletes.RemoteDeleteItems];
            if (scopedDirectoryDelete is not null)
            {
                foreach (string key in scopedDirectoryDelete.DirectoryKeys)
                {
                    remoteDirectoriesByPath.TryGetValue(key, out RemoteDirectorySnapshot? remote);
                    directoryStateByPath.TryGetValue(key, out SyncStateEntry? state);
                    remoteDeletePlanItems.Add(RemoteDeletePlanFingerprint.CreateDirectoryItem(
                        key,
                        remote?.Node.Id ?? state?.RemoteNodeId));
                }
            }

            return new SyncDeleteGuard(
                options,
                fileDeletes.LocalDeletes + directoryDeletes.LocalDeletes,
                remoteDeletePlanItems);
        }

        private static (int LocalDeletes, IReadOnlyList<string> RemoteDeleteItems) CountPlannedFileDeletes(
            IReadOnlyDictionary<string, SyncStateEntry> stateByPath,
            IReadOnlyDictionary<string, LocalFileSnapshot> localByPath,
            IReadOnlyDictionary<string, RemoteFileSnapshot> remoteByPath,
            IReadOnlySet<string>? scopedFileDeleteKeys,
            IReadOnlySet<string> scopedLocalDeletedFileKeys)
        {
            int localDeletes = 0;
            List<string> remoteDeleteItems = [];
            foreach (KeyValuePair<string, SyncStateEntry> state in stateByPath)
            {
                localByPath.TryGetValue(state.Key, out LocalFileSnapshot? local);
                remoteByPath.TryGetValue(state.Key, out RemoteFileSnapshot? remote);
                SyncDeleteDirection direction = GetPlannedDeleteDirection(
                    state.Value,
                    local,
                    remote,
                    scopedLocalDeletedFileKeys.Contains(state.Key));
                if (!IsScopedDeleteAllowed(scopedFileDeleteKeys, state.Key))
                {
                    continue;
                }

                switch (direction)
                {
                    case SyncDeleteDirection.None:
                        break;
                    case SyncDeleteDirection.Local:
                        localDeletes++;
                        break;
                    case SyncDeleteDirection.Remote:
                        remoteDeleteItems.Add(RemoteDeletePlanFingerprint.CreateFileItem(
                            state.Key,
                            remote?.File.Id ?? state.Value.RemoteFileId));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
                }
            }

            return (localDeletes, remoteDeleteItems);
        }

        private static (int LocalDeletes, IReadOnlyList<string> RemoteDeleteItems) CountPlannedDirectoryDeletes(
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath,
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            DirectoryContentIndex localDirectoryContentIndex,
            DirectoryContentIndex remoteDirectoryContentIndex,
            IReadOnlySet<string>? scopedDirectoryDeleteKeys,
            ScopedVirtualFilesDirectoryDeletePlan? scopedDirectoryDelete)
        {
            int localDeletes = 0;
            List<string> remoteDeleteItems = [];
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (scopedDirectoryDelete?.DirectoryKeys.Contains(state.Key, PathComparer) == true)
                {
                    continue;
                }

                localDirectoriesByPath.TryGetValue(state.Key, out LocalDirectorySnapshot? local);
                remoteDirectoriesByPath.TryGetValue(state.Key, out RemoteDirectorySnapshot? remote);
                SyncDeleteDirection direction = GetPlannedDirectoryDeleteDirection(
                    state.Value,
                    local,
                    remote,
                    remoteDirectoryContentIndex);
                if (!IsScopedDeleteAllowed(scopedDirectoryDeleteKeys, state.Key))
                {
                    continue;
                }

                switch (direction)
                {
                    case SyncDeleteDirection.None:
                        break;
                    case SyncDeleteDirection.Local:
                        localDeletes++;
                        break;
                    case SyncDeleteDirection.Remote:
                        remoteDeleteItems.Add(RemoteDeletePlanFingerprint.CreateDirectoryItem(
                            state.Key,
                            remote?.Node.Id ?? state.Value.RemoteNodeId));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
                }
            }

            return (localDeletes, remoteDeleteItems);
        }

        public static bool IsScopedDeleteAllowed(IReadOnlySet<string>? scopedDeleteKeys, string pathKey)
        {
            return scopedDeleteKeys is null || scopedDeleteKeys.Contains(pathKey);
        }

        public static bool HasLocalDirectoryDeleteCandidates(
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (state.Value.RemoteNodeId is null)
                {
                    continue;
                }

                if (localDirectoriesByPath.ContainsKey(state.Key) && !remoteDirectoriesByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool HasRemoteDirectoryDeleteCandidates(
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (state.Value.RemoteNodeId is null)
                {
                    continue;
                }

                if (!localDirectoriesByPath.ContainsKey(state.Key) && remoteDirectoriesByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool HasStaleDirectoryState(
            IReadOnlyDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<string, RemoteDirectorySnapshot> remoteDirectoriesByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            foreach (KeyValuePair<string, SyncStateEntry> state in directoryStateByPath)
            {
                if (!localDirectoriesByPath.ContainsKey(state.Key) && !remoteDirectoriesByPath.ContainsKey(state.Key))
                {
                    return true;
                }
            }

            return false;
        }

        private static SyncDeleteDirection GetPlannedDirectoryDeleteDirection(
            SyncStateEntry state,
            LocalDirectorySnapshot? local,
            RemoteDirectorySnapshot? remote,
            DirectoryContentIndex remoteDirectoryContentIndex)
        {
            if (state.RemoteNodeId is null)
            {
                return SyncDeleteDirection.None;
            }

            if (local is null)
            {
                return GetMissingLocalDirectoryDeleteDirection(
                    state,
                    remote,
                    remoteDirectoryContentIndex);
            }

            return remote is null ? SyncDeleteDirection.Local : SyncDeleteDirection.None;
        }

        private static SyncDeleteDirection GetMissingLocalDirectoryDeleteDirection(
            SyncStateEntry state,
            RemoteDirectorySnapshot? remote,
            DirectoryContentIndex remoteDirectoryContentIndex)
        {
            if (remote is null)
            {
                return SyncDeleteDirection.None;
            }

            return remoteDirectoryContentIndex.HasChildren(remote.RelativePath)
                ? SyncDeleteDirection.None
                : SyncDeleteDirection.Remote;
        }

        private static SyncDeleteDirection GetPlannedDeleteDirection(
            SyncStateEntry? state,
            LocalFileSnapshot? local,
            RemoteFileSnapshot? remote,
            bool exactLocalDelete)
        {
            if (state is null || (local is null && remote is null))
            {
                return SyncDeleteDirection.None;
            }

            if (IsMissingOnlineOnlyPlaceholder(state, local, remote))
            {
                return exactLocalDelete ? SyncDeleteDirection.Remote : SyncDeleteDirection.None;
            }

            if (LocalAndRemoteContentMatches(local, remote))
            {
                return SyncDeleteDirection.None;
            }

            return ToDeleteDirection(ResolveTrackedFileChange(CreateFileChangeState(state, local, remote)));
        }

        private static SyncDeleteDirection ToDeleteDirection(SyncFileChangeKind changeKind)
        {
            return changeKind switch
            {
                SyncFileChangeKind.DeleteLocal => SyncDeleteDirection.Local,
                SyncFileChangeKind.DeleteRemote => SyncDeleteDirection.Remote,
                SyncFileChangeKind.None => SyncDeleteDirection.None,
                SyncFileChangeKind.DeleteState => SyncDeleteDirection.None,
                SyncFileChangeKind.Upload => SyncDeleteDirection.None,
                SyncFileChangeKind.Download => SyncDeleteDirection.None,
                SyncFileChangeKind.Conflict => SyncDeleteDirection.None,
                _ => throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null)
            };
        }
    }
}
