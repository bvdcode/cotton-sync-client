// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Startup
{
    internal class ShellShareLinkSmokeClipboardService : IDesktopClipboardService
    {
        public string? CopiedText { get; private set; }

        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopiedText = text;
            return Task.CompletedTask;
        }
    }
}
