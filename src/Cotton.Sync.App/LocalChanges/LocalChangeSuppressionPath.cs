// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.App.LocalChanges
{
    internal static class LocalChangeSuppressionPath
    {
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;
        private const int FileAttributeRecallOnOpen = 0x00040000;
        private static readonly char[] DirectorySeparators =
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

        public static bool IsOnlineOnlyPlaceholder(string fullPath)
        {
            try
            {
                return IsOnlineOnlyAttributes(File.GetAttributes(fullPath));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return false;
            }
        }

        public static bool IsPinnedPlaceholder(string fullPath)
        {
            try
            {
                return HasRawAttribute(File.GetAttributes(fullPath), FileAttributePinned);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return false;
            }
        }

        public static bool IsOnlineOnlyAttributes(FileAttributes attributes)
        {
            return !HasRawAttribute(attributes, FileAttributePinned)
                && (HasRawAttribute(attributes, FileAttributeRecallOnOpen)
                || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                || (attributes & FileAttributes.Offline) != 0);
        }

        public static bool MatchesExpectedMetadata(string fullPath, LocalChangeSuppressionEntry entry)
        {
            try
            {
                FileInfo info = new FileInfo(fullPath);
                return info.Exists
                    && info.Length == entry.ExpectedSizeBytes
                    && (!entry.ExpectedLastWriteUtc.HasValue
                        || info.LastWriteTimeUtc == entry.ExpectedLastWriteUtc.Value);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return false;
            }
        }

        public static string ResolveInsideRoot(string localRootPath, string relativePath)
        {
            string normalizedRelativePath = SyncPath.Normalize(relativePath);
            string localRelativePath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Normalize(Path.Combine(localRootPath, localRelativePath));
            if (!IsInsideRoot(localRootPath, fullPath))
            {
                throw new ArgumentException("Suppression path must stay inside the local sync root.", nameof(relativePath));
            }

            return fullPath;
        }

        public static bool IsInsideRoot(string localRootPath, string fullPath)
        {
            string normalizedRoot = Normalize(localRootPath);
            string normalizedPath = Normalize(fullPath);
            string rootWithSeparator = normalizedRoot.TrimEnd(DirectorySeparators) + Path.DirectorySeparatorChar;
            return PathEquals(normalizedRoot, normalizedPath)
                || normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        public static bool PathEquals(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string fullPath)
        {
            string normalized = Path.GetFullPath(fullPath);
            string? root = Path.GetPathRoot(normalized);
            if (!string.IsNullOrEmpty(root)
                && string.Equals(
                    normalized.TrimEnd(DirectorySeparators),
                    root.TrimEnd(DirectorySeparators),
                    StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return normalized.TrimEnd(DirectorySeparators);
        }

        private static bool HasRawAttribute(FileAttributes attributes, int rawAttribute)
        {
            return (((int)attributes) & rawAttribute) == rawAttribute;
        }
    }
}
