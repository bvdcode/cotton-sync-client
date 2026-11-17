// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal interface IDesktopUpdateInstaller
    {
        Task<DesktopUpdateInstallResult> StartInstallAsync(
            string installerPath,
            bool launchAfterUpdate,
            CancellationToken cancellationToken = default);
    }
}
