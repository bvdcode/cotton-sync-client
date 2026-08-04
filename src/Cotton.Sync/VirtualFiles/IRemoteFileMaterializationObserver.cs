// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.VirtualFiles
{
    /// <summary>
    /// Observes provider-originated remote file materialization before the local filesystem is changed.
    /// </summary>
    public interface IRemoteFileMaterializationObserver
    {
        /// <summary>
        /// Runs before the sync engine writes remote content to a local file.
        /// </summary>
        Task BeforeWriteFileAsync(
            RemoteFileMaterializationRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs after remote content has been written to the local file successfully.
        /// </summary>
        Task AfterWriteFileAsync(
            RemoteFileMaterializationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
