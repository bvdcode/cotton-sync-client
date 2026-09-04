// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Local
{
    /// <summary>
    /// Describes a committed local write and any concurrent local version preserved beside it.
    /// </summary>
    public record LocalFileWriteResult(string? ConflictRelativePath = null);
}
