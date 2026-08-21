// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Startup
{
    internal record DesktopShellShareLinkResult(
        bool IsApiAvailable,
        bool IsCreated,
        string? ShareLink,
        string? FailureReason)
    {
        public static DesktopShellShareLinkResult Unavailable(string failureReason) =>
            new(false, false, null, failureReason);

        public static DesktopShellShareLinkResult Failed(string failureReason) =>
            new(true, false, null, failureReason);

        public static DesktopShellShareLinkResult Created(Uri shareUri) =>
            new(true, true, shareUri.AbsoluteUri, null);
    }
}
