// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal record AlwaysKeepPopulationPaths(
        string FolderPath,
        IReadOnlyList<string> DirectoryPaths,
        IReadOnlyList<string> FilePaths);
}
