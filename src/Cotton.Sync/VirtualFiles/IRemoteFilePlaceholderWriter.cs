// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Creates local virtual-files placeholders for remote-only files without downloading file content.
    /// </summary>
    public interface IRemoteFilePlaceholderWriter
    {
        /// <summary>
        /// Creates or updates a local placeholder for the supplied remote file.
        /// </summary>
        Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
            RemoteFilePlaceholderRequest request,
            CancellationToken cancellationToken = default);
    }
}
