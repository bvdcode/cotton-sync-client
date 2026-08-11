// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal record SyncDeletePlan(
        SyncDeleteGuard DeleteGuard,
        DirectoryContentIndex LocalDirectoryContentIndex,
        DirectoryContentIndex RemoteDirectoryContentIndex,
        IReadOnlySet<string>? ScopedFileDeleteKeys,
        IReadOnlySet<string>? ScopedDirectoryDeleteKeys,
        IReadOnlySet<string> ScopedLocalDeletedFileKeys,
        ScopedVirtualFilesDirectoryDeletePlan? ScopedDirectoryDelete,
        bool HasLocalDirectoryDeleteCandidates,
        bool RequiresDirectoryReconciliation,
        bool HasMissingRemoteOnlyPlaceholder);
}
