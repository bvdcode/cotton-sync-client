// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal record ScopedVirtualFilesDirectoryRenameCandidate(
        string SourceKey,
        string SourcePath,
        string TargetKey,
        string TargetPath);
}
