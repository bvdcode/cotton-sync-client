// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Indicates that a download must restart from the latest remote file manifest.
    /// </summary>
    public class RemoteFileVersionChangedException(string relativePath, Exception innerException)
        : IOException($"Remote file changed during download: {relativePath}.", innerException)
    {
    }
}
