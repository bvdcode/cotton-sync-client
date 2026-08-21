// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;

namespace Cotton.Sync.Desktop.Shell
{
    internal readonly record struct RestoredSession(AuthSession Session, int Attempts);
}
