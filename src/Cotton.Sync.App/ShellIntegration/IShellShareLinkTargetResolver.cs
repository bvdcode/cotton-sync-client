// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.ShellIntegration
{
    public interface IShellShareLinkTargetResolver
    {
        Task<ShellShareLinkTarget> ResolveAsync(
            string localPath,
            CancellationToken cancellationToken = default);
    }
}
