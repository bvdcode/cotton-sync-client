// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Remote
{
    /// <summary>
    /// Receives remote tree entries as they are discovered.
    /// </summary>
    public interface IRemoteTreeStreamSink
    {
        /// <summary>
        /// Adds one discovered remote directory.
        /// </summary>
        ValueTask AddDirectoryAsync(RemoteDirectorySnapshot directory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds one discovered remote file.
        /// </summary>
        ValueTask AddFileAsync(RemoteFileSnapshot file, CancellationToken cancellationToken = default);
    }
}
