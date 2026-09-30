// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal record LiveSyncSmokeRestoreFile(string RelativePath, Guid RemoteFileId, string Sha256);
}
