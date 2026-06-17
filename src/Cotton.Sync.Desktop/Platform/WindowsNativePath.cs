// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsNativePath
    {
        public static string ToWin32FilePath(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return path;
            }

            if (path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\?\GLOBALROOT" + path;
            }

            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + fullPath.TrimStart('\\');
            }

            return @"\\?\" + fullPath;
        }
    }
}
