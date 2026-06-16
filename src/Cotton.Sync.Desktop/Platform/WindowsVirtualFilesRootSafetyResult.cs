// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed record WindowsVirtualFilesRootSafetyResult(
        bool IsSafe,
        WindowsVirtualFilesRootSafetyIssue Issue,
        string FullPath,
        string Details)
    {
        public static WindowsVirtualFilesRootSafetyResult Safe(string fullPath)
        {
            return new WindowsVirtualFilesRootSafetyResult(
                true,
                WindowsVirtualFilesRootSafetyIssue.None,
                fullPath,
                "Virtual-files sync root is safe to register.");
        }

        public static WindowsVirtualFilesRootSafetyResult Unsafe(
            WindowsVirtualFilesRootSafetyIssue issue,
            string fullPath,
            string details)
        {
            return new WindowsVirtualFilesRootSafetyResult(false, issue, fullPath, details);
        }
    }
}
