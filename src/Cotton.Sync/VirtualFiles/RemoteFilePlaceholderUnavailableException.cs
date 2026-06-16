// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Indicates that a virtual-files placeholder cannot be created for a recoverable, user-actionable reason.
    /// </summary>
    public sealed class RemoteFilePlaceholderUnavailableException : Exception
    {
        public RemoteFilePlaceholderUnavailableException(string relativePath, string reason)
            : base(CreateMessage(relativePath, reason))
        {
            RelativePath = SyncPath.Normalize(relativePath);
            Reason = string.IsNullOrWhiteSpace(reason)
                ? "Virtual-files placeholder creation is unavailable."
                : reason.Trim();
        }

        /// <summary>
        /// Gets the normalized placeholder path.
        /// </summary>
        public string RelativePath { get; }

        /// <summary>
        /// Gets the user-actionable reason.
        /// </summary>
        public string Reason { get; }

        private static string CreateMessage(string relativePath, string reason)
        {
            string normalizedPath = string.IsNullOrWhiteSpace(relativePath)
                ? "item"
                : SyncPath.Normalize(relativePath);
            string normalizedReason = string.IsNullOrWhiteSpace(reason)
                ? "Virtual-files placeholder creation is unavailable."
                : reason.Trim();
            return normalizedPath + ": " + normalizedReason;
        }
    }
}
