// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Indicates that a complete, consistent remote directory listing is temporarily unavailable.
    /// </summary>
    /// <param name="message">The directory listing validation failure.</param>
    public class RemoteDirectoryListingUnavailableException(string message) : IOException(message)
    {
    }
}
