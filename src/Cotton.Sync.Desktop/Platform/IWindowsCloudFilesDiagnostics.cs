// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal interface IWindowsCloudFilesDiagnostics
    {
        void Record(
            string operation,
            string status,
            string? syncPairId = null,
            string? localRootPath = null,
            string? relativePath = null,
            string? details = null,
            int? hResult = null);

        IReadOnlyList<WindowsCloudFilesDiagnosticEvent> Snapshot();
    }
}
