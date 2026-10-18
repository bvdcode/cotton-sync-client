// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Shell
{
    internal static partial class DesktopActionRequiredMessageResolver
    {
        internal static string GetIssueTitle(string? message)
        {
            return VirtualFileUserFacingCopy.IsMetadataIncompleteMessage(message)
                ? "Sync incomplete"
                : "Action required";
        }
    }
}
