// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Desktop.Updates
{
    internal interface IDesktopUpdateInstallerProcessLauncher
    {
        DesktopUpdateInstallResult Start(ProcessStartInfo startInfo);
    }
}
