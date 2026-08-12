// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Local
{
    /// <summary>
    /// Checks whether a specific file still exists inside a local sync root.
    /// </summary>
    public interface ILocalFilePresenceProbe
    {
        /// <summary>
        /// Returns whether the relative file path currently exists inside the root.
        /// </summary>
        bool FileExists(string rootPath, string relativePath);
    }
}
