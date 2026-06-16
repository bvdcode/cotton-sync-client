// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal interface IDesktopUpdateInstaller
    {
        void StartSilentInstall(
            string installerPath,
            bool launchAfterUpdate);
    }
}
