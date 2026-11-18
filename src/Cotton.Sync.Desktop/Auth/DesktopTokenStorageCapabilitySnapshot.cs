// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Auth
{
    internal record DesktopTokenStorageCapabilitySnapshot(
        string Scheme,
        bool IsReleaseSecure,
        string Details,
        bool IsUserApproved = false)
    {
        public bool IsUsable => IsReleaseSecure || IsUserApproved;
    }
}
