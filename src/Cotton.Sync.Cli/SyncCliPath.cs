// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Cli
{
    internal static class SyncCliPath
    {
        public static bool AreSameOrNested(string firstPath, string secondPath)
        {
            string first = NormalizeFullPath(firstPath);
            string second = NormalizeFullPath(secondPath);
            StringComparison comparison = GetPathComparison();
            return string.Equals(first, second, comparison)
                || second.StartsWith(EnsureTrailingSeparator(first), comparison)
                || first.StartsWith(EnsureTrailingSeparator(second), comparison);
        }

        public static bool AreSame(string firstPath, string secondPath)
        {
            return string.Equals(NormalizeFullPath(firstPath), NormalizeFullPath(secondPath), GetPathComparison());
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, GetPathComparison()))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static StringComparison GetPathComparison()
        {
            return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }
    }
}
