// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    internal record ScopedVirtualFilesDirectoryDeletePlan(
        IReadOnlyList<string> RootPaths,
        IReadOnlyList<string> DirectoryKeys,
        IReadOnlyList<string> FileKeys,
        IReadOnlyList<string> FilePaths);
}
