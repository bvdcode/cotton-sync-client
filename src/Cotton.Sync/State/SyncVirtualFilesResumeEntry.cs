// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.State
{
    /// <summary>
    /// Represents the compact persisted state needed to resume Windows virtual-files seeding.
    /// </summary>
    public readonly record struct SyncVirtualFilesResumeEntry(
        string RelativePath,
        SyncEntryKind Kind,
        Guid? RemoteNodeId,
        Guid? RemoteFileId,
        string? RemoteContentHash,
        string? RemoteETag,
        SyncPlaceholderHydrationState PlaceholderHydrationState,
        bool HasPlaceholderIdentity);
}
