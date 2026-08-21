// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal readonly record struct TextReadSnapshot(
        bool Exists,
        bool Read,
        string? Content,
        string Details);
}
