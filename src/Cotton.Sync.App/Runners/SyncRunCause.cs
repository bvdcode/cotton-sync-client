// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.Runners
{
    /// <summary>
    /// Identifies the events that requested a synchronization pass.
    /// </summary>
    [Flags]
    public enum SyncRunCause
    {
        None = 0,
        Manual = 1,
        Periodic = 2,
        RealtimeRemoteChange = 4,
        LocalChange = 8,
        LocalWatcherError = 16,
        LocalChangeOverflow = 32,
        LocalRenameRecovery = 64,
        RemoteCursorExpired = 128,
        InitialPopulation = 256,
        InternalMaintenance = 512,
        Resume = 1024,
    }
}
