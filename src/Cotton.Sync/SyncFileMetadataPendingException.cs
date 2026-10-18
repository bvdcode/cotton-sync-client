// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync
{
    /// <summary>
    /// Reports a completed content reconciliation with unfinished file metadata recorded in sync state.
    /// </summary>
    public class SyncFileMetadataPendingException(string message) : SyncActionRequiredException(message)
    {
    }
}
