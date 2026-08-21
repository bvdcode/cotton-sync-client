// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.ShellIntegration;

namespace Cotton.Sync.Desktop.Startup
{
    internal class ShellShareLinkSmokeClient(DesktopShellShareLinkResult result) : IDesktopShellShareLinkClient
    {
        public Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
            ShellShareLinkTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
