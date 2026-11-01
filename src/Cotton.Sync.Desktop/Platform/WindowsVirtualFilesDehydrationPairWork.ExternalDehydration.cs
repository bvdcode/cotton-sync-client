// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private async Task<bool> TryCompleteExternalDehydrationAsync(
            SyncPairSettings syncPair,
            SyncStateEntry state,
            WindowsVirtualFileDiskState diskState,
            CancellationToken cancellationToken)
        {
            if (!MaterializedBaselineMatches(state, diskState)
                || !IsMatchingOnlineOnlyPlaceholder(syncPair, state, diskState))
            {
                return false;
            }

            await MarkDehydratedAsync(state, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "external-dehydration",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                state.RelativePath,
                "Windows made the unchanged tracked file online-only; local availability was updated without reading content.");
            return true;
        }
    }
}
