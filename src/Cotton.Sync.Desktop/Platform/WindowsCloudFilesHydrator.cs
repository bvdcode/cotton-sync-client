// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesHydrator(
        IWindowsCloudFilesAdapter adapter,
        WindowsCloudFilesHydrationCoordinator coordinator) : IWindowsCloudFilesHydrator
    {
        public async Task HydratePlaceholderAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsCloudFilesPlaceholderState state = adapter.GetPlaceholderState(syncPair, relativePath);
            if (state.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder)
                && state.HasFlag(WindowsCloudFilesPlaceholderState.Partial)
                && state != WindowsCloudFilesPlaceholderState.Invalid)
            {
                WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(
                    adapter.GetPlaceholderIdentity(syncPair, relativePath));
                using IDisposable prepared = await coordinator.PrepareHydrationAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
                await HydrateAsync(syncPair, relativePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            await HydrateAsync(syncPair, relativePath, cancellationToken).ConfigureAwait(false);
        }

        private Task HydrateAsync(SyncPairSettings syncPair, string relativePath, CancellationToken cancellationToken)
        {
            return Task.Run(() => adapter.HydratePlaceholder(syncPair, relativePath), cancellationToken);
        }
    }
}
