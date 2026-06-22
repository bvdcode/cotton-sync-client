// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.State
{
    /// <summary>
    /// Tracks how much local content is currently materialized for a virtual-files placeholder.
    /// </summary>
    public enum SyncPlaceholderHydrationState
    {
        /// <summary>
        /// The entry is not tracked as a virtual-files placeholder.
        /// </summary>
        None = 0,

        /// <summary>
        /// The entry is visible locally as a placeholder and content is remote-only.
        /// </summary>
        RemoteOnly = 1,

        /// <summary>
        /// The entry has local content materialized.
        /// </summary>
        Hydrated = 2,

        /// <summary>
        /// The entry was dehydrated after content had previously been materialized.
        /// </summary>
        Dehydrated = 3,

        /// <summary>
        /// The latest hydration request failed and can be retried.
        /// </summary>
        HydrationFailed = 4,
    }
}
