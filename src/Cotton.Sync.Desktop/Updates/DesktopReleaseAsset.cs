// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal record DesktopReleaseAsset(
        string Name,
        string Sha256,
        long SizeBytes,
        Uri Url);
}
