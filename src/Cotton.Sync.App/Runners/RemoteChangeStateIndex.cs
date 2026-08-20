// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.App.Runners
{
    internal class RemoteChangeStateIndex
    {
        private readonly Dictionary<Guid, string> _filePathById = [];
        private readonly Dictionary<Guid, string> _nodePathById = [];

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
