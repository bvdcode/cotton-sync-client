// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.ShellIntegration;

namespace Cotton.Sync.Desktop.Startup
{
    internal interface IDesktopShellShareLinkClient
    {
        Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
            ShellShareLinkTarget target,
            CancellationToken cancellationToken = default);
    }
}
