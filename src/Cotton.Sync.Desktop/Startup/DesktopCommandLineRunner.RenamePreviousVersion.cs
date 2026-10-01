// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopCommandLineRunner
    {
        private static async Task<bool> WaitForLiveRenamedPreviousVersionAsync(
            string receiverRoot,
            Guid receiverPairId,
            SqliteSyncStateStore receiverStore,
            string relativePath,
            Guid remoteFileId,
            string previousContent,
            CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + RestoreManifestHoldTimeout;
            do
            {
                SyncStateEntry? entry = await receiverStore.GetAsync(
                    receiverPairId.ToString("D"), relativePath, cancellationToken).ConfigureAwait(false);
                if (entry?.RemoteFileId == remoteFileId)
                {
                    TextReadSnapshot read = await TryReadAllTextForLiveSmokeAsync(
                        FullPath(receiverRoot, relativePath), cancellationToken).ConfigureAwait(false);
                    if (read.Read && read.Content == previousContent)
                    {
                        return true;
                    }
                }
                await Task.Delay(PropagationPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }
    }
}
