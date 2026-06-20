// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.ShellIntegration
{
    public record ShellShareLinkTarget(
        ShellShareLinkTargetStatus Status,
        Guid? SyncPairId = null,
        string RelativePath = "",
        ShellShareLinkTargetKind Kind = ShellShareLinkTargetKind.Unknown,
        Guid? RemoteNodeId = null,
        Guid? RemoteFileId = null)
    {
        public bool CanCreateShareLink => Status == ShellShareLinkTargetStatus.Resolved
            && (RemoteNodeId.HasValue || RemoteFileId.HasValue);
    }
}
