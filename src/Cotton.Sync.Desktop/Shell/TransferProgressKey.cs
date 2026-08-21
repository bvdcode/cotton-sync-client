// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Shell
{
    internal readonly record struct TransferProgressKey(
        Guid SyncPairId,
        SyncTransferDirection Direction,
        string RelativePath);
}
