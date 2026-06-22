// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    [Flags]
    internal enum WindowsCloudFilesPlaceholderState : uint
    {
        None = 0x00000000,
        Placeholder = 0x00000001,
        SyncRoot = 0x00000002,
        EssentialPropertyPresent = 0x00000004,
        InSync = 0x00000008,
        Partial = 0x00000010,
        PartiallyOnDisk = 0x00000020,
        Invalid = 0xffffffff,
    }
}
