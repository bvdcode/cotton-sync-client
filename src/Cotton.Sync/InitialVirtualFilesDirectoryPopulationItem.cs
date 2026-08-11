// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Remote;

namespace Cotton.Sync
{
    internal record InitialVirtualFilesDirectoryPopulationItem(RemoteDirectorySnapshot Directory)
        : InitialVirtualFilesPopulationItem;
}
