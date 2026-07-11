// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;

namespace Cotton.Sync.Desktop.Shell
{
    internal record DesktopStoredSessionRestoreSnapshot(
        AuthSession? Session,
        bool HasStoredSession,
        string? ErrorMessage);
}
