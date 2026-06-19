// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal interface IDesktopUpdateService
    {
        Task<DesktopUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

        Task<DesktopUpdateDownloadResult> DownloadInstallerAsync(
            DesktopUpdateCheckResult checkResult,
            IProgress<DesktopUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
