// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.SyncPairs
{
    /// <summary>
    /// Describes which synchronization modes are available on the current host.
    /// </summary>
    public sealed record SyncPairModeCapabilitySnapshot(
        bool IsWindowsVirtualFilesSupported,
        string WindowsVirtualFilesDetails)
    {
        public static SyncPairModeCapabilitySnapshot FullMirrorOnly { get; } =
            new(false, "Windows virtual files are not supported on this host.");

        public bool IsSupported(SyncPairMode mode)
        {
            return mode switch
            {
                SyncPairMode.FullMirror => true,
                SyncPairMode.WindowsVirtualFiles => IsWindowsVirtualFilesSupported,
                _ => false,
            };
        }

        public string GetUnsupportedMessage(SyncPairMode mode)
        {
            return mode switch
            {
                SyncPairMode.Unknown => "The selected sync mode is not valid.",
                SyncPairMode.WindowsVirtualFiles => string.IsNullOrWhiteSpace(WindowsVirtualFilesDetails)
                    ? "Windows virtual files are not supported on this host."
                    : WindowsVirtualFilesDetails,
                _ => "The selected sync mode is not implemented yet.",
            };
        }
    }
}
