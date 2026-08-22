// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesPinStateResolver(Func<string, FileAttributes> readFileAttributes)
    {
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;

        public WindowsCloudFilesPinState? ReadExisting(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = readFileAttributes(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (HasRawAttribute(attributes, FileAttributePinned))
            {
                return WindowsCloudFilesPinState.Pinned;
            }

            if (HasRawAttribute(attributes, FileAttributeUnpinned))
            {
                return WindowsCloudFilesPinState.Unpinned;
            }

            return null;
        }

        public WindowsCloudFilesPinState ResolveNew(string parentDirectoryPath)
        {
            return ReadExisting(parentDirectoryPath) == WindowsCloudFilesPinState.Pinned
                ? WindowsCloudFilesPinState.Inherit
                : WindowsCloudFilesPinState.Unpinned;
        }

        private static bool HasRawAttribute(FileAttributes attributes, int attribute)
        {
            return ((int)attributes & attribute) != 0;
        }
    }
}
