// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    /// <summary>
    /// Defines how remote-only files are materialized under the local sync root.
    /// </summary>
    public enum SyncPairMaterializationMode
    {
        /// <summary>
        /// Downloads remote-only files into a complete local mirror.
        /// </summary>
        FullMirror = 0,

        /// <summary>
        /// Uses the Windows Cloud Files placeholder surface for remote-only files.
        /// </summary>
        WindowsVirtualFiles = 1,
    }
}
