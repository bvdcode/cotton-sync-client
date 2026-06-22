// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class UnsupportedDesktopClipboardService : IDesktopClipboardService
    {
        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            throw new NotSupportedException("Desktop clipboard is unavailable.");
        }
    }
}
