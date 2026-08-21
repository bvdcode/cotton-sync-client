// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesPathGuard(
        Func<string, bool> isReparsePoint,
        Func<string, bool> isCloudFilesReparsePoint)
    {
        public void EnsureNoForeignReparsePointDescendant(
            string syncRootPath,
            string targetDirectoryPath)
        {
            string root = Path.GetFullPath(syncRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(targetDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relative = Path.GetRelativePath(root, target);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException("Virtual-files placeholder path escaped the sync root.");
            }

            string current = root;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
                {
                    continue;
                }

                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current))
                    && isReparsePoint(current)
                    && !isCloudFilesReparsePoint(current))
                {
                    throw new InvalidOperationException(
                        "Virtual-files placeholder path cannot traverse a reparse point.");
                }
            }
        }
    }
}
