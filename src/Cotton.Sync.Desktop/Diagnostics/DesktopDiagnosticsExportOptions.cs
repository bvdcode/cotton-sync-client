// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal sealed record DesktopDiagnosticsExportOptions(bool IncludePrivateSupportData)
    {
        public static DesktopDiagnosticsExportOptions Public { get; } = new(false);

        public static DesktopDiagnosticsExportOptions PrivateSupport { get; } = new(true);

        public string FileNameSegment => IncludePrivateSupportData ? "-private-support" : string.Empty;

        public string DisplayName => IncludePrivateSupportData ? "private-support" : "public";
    }
}
