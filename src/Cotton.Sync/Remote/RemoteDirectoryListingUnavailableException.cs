// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Indicates that a complete, consistent remote directory listing is temporarily unavailable.
    /// </summary>
    /// <param name="message">The directory listing validation failure.</param>
    /// <param name="innerException">The failure that invalidated the directory listing.</param>
    public class RemoteDirectoryListingUnavailableException(string message, Exception? innerException = null)
        : IOException(message, innerException)
    {
    }
}
