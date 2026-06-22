// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.SyncApplication
{
    /// <summary>
    /// Starts and stops a background component whose lifetime is tied to sync-core runtime state.
    /// </summary>
    public interface ISyncCoreLifecycleComponent
    {
        /// <summary>
        /// Starts the background component.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the background component.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);
    }
}
