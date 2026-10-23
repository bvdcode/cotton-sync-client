// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;
using Cotton.Sync.State;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal static class RemoteDeleteConfirmation
    {
        public static IReadOnlySet<string> CreatePathKeys(
            RemoteChangeFeedSnapshot changes,
            IReadOnlyDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<string, SyncStateEntry> directoryStateByPath)
        {
            HashSet<string> confirmed = new(StringComparer.OrdinalIgnoreCase);
            if (!changes.ContainsDeletes)
            {
                return confirmed;
            }

            Dictionary<Guid, RemoteChangeImpact> fileChanges = [];
            Dictionary<Guid, RemoteChangeImpact> directoryChanges = [];
            foreach (RemoteChangeImpact change in changes.Changes)
            {
                switch (change.TargetKind)
                {
                    case RemoteChangeTargetKind.File when change.NodeFileId.HasValue:
                        fileChanges[change.NodeFileId.Value] = change;
                        break;
                    case RemoteChangeTargetKind.Folder when change.NodeId.HasValue:
                        directoryChanges[change.NodeId.Value] = change;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(changes), change.TargetKind, null);
                }
            }

            foreach (SyncStateEntry state in fileStateByPath.Values.Concat(directoryStateByPath.Values))
            {
                RemoteChangeImpact? latestChange = null;
                if (state.Kind == SyncEntryKind.File && state.RemoteFileId.HasValue)
                {
                    fileChanges.TryGetValue(state.RemoteFileId.Value, out latestChange);
                }

                string ancestorPath = state.Kind switch
                {
                    SyncEntryKind.File => GetParentPath(state.RelativePath),
                    SyncEntryKind.Directory => state.RelativePath,
                    _ => throw new ArgumentOutOfRangeException(nameof(state), state.Kind, null),
                };
                while (latestChange?.Action != RemoteChangeAction.Deleted && !string.IsNullOrEmpty(ancestorPath))
                {
                    if (directoryStateByPath.TryGetValue(SyncPath.ToKey(ancestorPath), out SyncStateEntry? directory)
                        && directory.RemoteNodeId.HasValue
                        && directoryChanges.TryGetValue(directory.RemoteNodeId.Value, out RemoteChangeImpact? change)
                        && (latestChange is null || change.Cursor > latestChange.Cursor))
                    {
                        latestChange = change;
                    }

                    ancestorPath = GetParentPath(ancestorPath);
                }

                if (latestChange?.Action == RemoteChangeAction.Deleted)
                {
                    confirmed.Add(SyncPath.ToKey(state.RelativePath));
                }
            }

            return confirmed;
        }
    }
}
