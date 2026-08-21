// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Collections.Concurrent;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsVirtualFilesDehydrationPairWork
    {
        private async Task<bool> TryHandleManualDehydrationAsync(
            SyncPairSettings syncPair,
            string relativePath,
            Action<string>? dehydrationStarting,
            CancellationToken cancellationToken)
        {
            (string NormalizedPath, string FullPath, SyncStateEntry State, WindowsVirtualFileDiskState DiskState)?
                context = await TryResolveTrackedVirtualFileAsync(syncPair, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!context.HasValue)
            {
                return false;
            }

            (string normalizedPath, string fullPath, SyncStateEntry state, WindowsVirtualFileDiskState diskState) =
                context.Value;

            if (IsCompletedManualFreeUpSpaceCandidate(diskState.Attributes))
            {
                return await CompleteAlreadyDehydratedFileAsync(
                        syncPair,
                        normalizedPath,
                        state,
                        dehydrationStarting,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (IsManualPinRemovalFileCandidate(diskState.Attributes)
                && MaterializedBaselineMatches(state, diskState))
            {
                RecordManualFileUnpinned(syncPair, normalizedPath);
                return true;
            }

            if (IsCompletedOnDemandHydrationCandidate(state, diskState.Attributes))
            {
                return await CompleteOnDemandHydrationAsync(
                        syncPair,
                        normalizedPath,
                        fullPath,
                        state,
                        diskState,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!IsManualFreeUpSpaceCandidate(diskState.Attributes))
            {
                return false;
            }

            if (!SizeMatchesBaseline(state, diskState.Length))
            {
                RecordSkipped(syncPair, normalizedPath, "Local size differs from the tracked remote file.");
                return false;
            }

            bool dehydrated = await DehydrateTrackedFileAsync(
                    syncPair,
                    normalizedPath,
                    state,
                    dehydrationStarting,
                    cancellationToken)
                .ConfigureAwait(false);
            return dehydrated;
        }

        private async Task<bool> CompleteAlreadyDehydratedFileAsync(
            SyncPairSettings syncPair,
            string normalizedPath,
            SyncStateEntry state,
            Action<string>? dehydrationStarting,
            CancellationToken cancellationToken)
        {
            dehydrationStarting?.Invoke(normalizedPath);
            await MarkDehydratedAsync(state, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "manual-free-up-space",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Free up space had already dehydrated the tracked placeholder.");
            return true;
        }

        private void RecordManualFileUnpinned(SyncPairSettings syncPair, string normalizedPath)
        {
            _diagnostics.Record(
                "manual-always-keep",
                "unpinned",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer removed Always keep on this device without changing materialized file content.");
        }

        private async Task<bool> CompleteOnDemandHydrationAsync(
            SyncPairSettings syncPair,
            string normalizedPath,
            string fullPath,
            SyncStateEntry state,
            WindowsVirtualFileDiskState diskState,
            CancellationToken cancellationToken)
        {
            if (!SizeMatchesBaseline(state, diskState.Length)
                || !await ContentMatchesRemoteAsync(state, normalizedPath, fullPath, diskState, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }

            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = state.RemoteContentHash;
            state.LocalLastWriteUtc = diskState.LastWriteUtc;
            state.LocalSizeBytes = diskState.Length;
            state.SyncedAtUtc = DateTime.UtcNow;
            await _stateStore.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "on-demand-hydration",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Opening the tracked online-only placeholder completed on-demand hydration.");
            return true;
        }

        private async Task<bool> DehydrateTrackedFileAsync(
            SyncPairSettings syncPair,
            string normalizedPath,
            SyncStateEntry state,
            Action<string>? dehydrationStarting,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(state.RemoteContentHash))
            {
                RecordSkipped(syncPair, normalizedPath, "Tracked remote content hash is missing.");
                return false;
            }

            bool dehydrated = await _cloudFiles
                .DehydratePlaceholderIfContentMatchesAsync(
                    syncPair,
                    normalizedPath,
                    state.RemoteContentHash,
                    () =>
                    {
                        dehydrationStarting?.Invoke(normalizedPath);
                        _localChangeSuppression?.SuppressProviderWrite(
                            syncPair.Id,
                            syncPair.LocalRootPath,
                            normalizedPath);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!dehydrated)
            {
                RecordSkipped(syncPair, normalizedPath, "Local content differs from the tracked remote file.");
                return false;
            }

            await MarkDehydratedAsync(state, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "manual-free-up-space",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Free up space dehydrated the tracked placeholder.");
            return true;
        }

        private async Task<(
            string NormalizedPath,
            string FullPath,
            SyncStateEntry State,
            WindowsVirtualFileDiskState DiskState)?> TryResolveTrackedVirtualFileAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return null;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (state is null || !IsTrackedVirtualFile(state))
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = ResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }

            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            return diskState is null
                ? null
                : (normalizedPath, fullPath, state, diskState);
        }

        private async Task MarkDehydratedAsync(
            SyncStateEntry state,
            CancellationToken cancellationToken)
        {
            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Dehydrated;
            state.LocalContentHash = null;
            state.LocalLastWriteUtc = null;
            state.LocalSizeBytes = null;
            state.SyncedAtUtc = DateTime.UtcNow;
            await _stateStore.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> ContentMatchesRemoteAsync(
            SyncStateEntry state,
            string normalizedPath,
            string fullPath,
            WindowsVirtualFileDiskState diskState,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(state.RemoteContentHash))
            {
                return false;
            }

            var local = new LocalFileSnapshot
            {
                RelativePath = normalizedPath,
                FullPath = fullPath,
                SizeBytes = diskState.Length,
                LastWriteUtc = diskState.LastWriteUtc,
            };
            string hash = await _contentHasher.ComputeContentHashAsync(local, cancellationToken).ConfigureAwait(false);
            return string.Equals(hash, state.RemoteContentHash, StringComparison.OrdinalIgnoreCase);
        }

        private void RecordSkipped(SyncPairSettings syncPair, string normalizedPath, string details)
        {
            _diagnostics.Record(
                "manual-free-up-space",
                "skipped",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                details);
        }

        private void RecordFailed(SyncPairSettings syncPair, string normalizedPath, string details)
        {
            _diagnostics.Record(
                "manual-always-keep",
                "failed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                details);
        }
    }
}
