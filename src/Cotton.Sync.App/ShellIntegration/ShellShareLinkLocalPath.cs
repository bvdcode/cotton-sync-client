// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.ShellIntegration
{
    internal readonly record struct ShellShareLinkLocalPath(string Value, bool WindowsStyle)
    {
        public int Length => Value.Length;

        public static bool TryNormalize(string value, out ShellShareLinkLocalPath path)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                path = default;
                return false;
            }

            path = Normalize(value);
            return true;
        }

        public static ShellShareLinkLocalPath Normalize(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            string trimmed = value.Trim();
            bool windowsStyle = IsWindowsStyle(trimmed);
            char separator = windowsStyle ? '\\' : '/';
            string normalized = windowsStyle
                ? trimmed.Replace('/', '\\')
                : trimmed.Replace('\\', '/');
            normalized = TrimTrailingSeparators(normalized, separator, windowsStyle);
            return new ShellShareLinkLocalPath(normalized, windowsStyle);
        }

        public bool ContainsOrEquals(ShellShareLinkLocalPath candidate)
        {
            if (WindowsStyle != candidate.WindowsStyle)
            {
                return false;
            }

            StringComparison comparison = WindowsStyle
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(Value, candidate.Value, comparison))
            {
                return true;
            }

            char separator = WindowsStyle ? '\\' : '/';
            return candidate.Value.StartsWith(Value + separator, comparison);
        }

        public string GetRelativePath(ShellShareLinkLocalPath candidate)
        {
            StringComparison comparison = WindowsStyle
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!ContainsOrEquals(candidate)
                || string.Equals(Value, candidate.Value, comparison))
            {
                return string.Empty;
            }

            string relative = candidate.Value[(Value.Length + 1)..];
            return WindowsStyle ? relative.Replace('\\', '/') : relative;
        }

        private static bool IsWindowsStyle(string path)
        {
            return path.Contains('\\', StringComparison.Ordinal)
                || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');
        }

        private static string TrimTrailingSeparators(
            string path,
            char separator,
            bool windowsStyle)
        {
            int minimumLength = windowsStyle && path.Length >= 3 && path[1] == ':' && path[2] == separator
                ? 3
                : 1;
            while (path.Length > minimumLength && path[^1] == separator)
            {
                path = path[..^1];
            }

            return path;
        }
    }
}
