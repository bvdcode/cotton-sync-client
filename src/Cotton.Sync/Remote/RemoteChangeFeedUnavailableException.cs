// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Indicates that the server-side desktop change feed is temporarily unavailable.
    /// </summary>
    public class RemoteChangeFeedUnavailableException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteChangeFeedUnavailableException" /> class.
        /// </summary>
        public RemoteChangeFeedUnavailableException(Exception innerException)
            : base(
                "Cotton Cloud desktop change feed is temporarily unavailable. Cotton Sync will retry automatically.",
                innerException)
        {
            ArgumentNullException.ThrowIfNull(innerException);
        }
    }
}
