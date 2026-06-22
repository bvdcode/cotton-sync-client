// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal enum WindowsVirtualFilesRootSafetyIssue
    {
        None = 0,
        EmptyPath,
        RelativePath,
        DriveRoot,
        UserProfileRoot,
        WindowsRoot,
        ProgramFilesRoot,
        RepositoryRoot,
        InvalidPath,
    }
}
