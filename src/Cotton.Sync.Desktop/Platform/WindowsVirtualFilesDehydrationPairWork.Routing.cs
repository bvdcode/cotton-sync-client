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
        private async Task RecoverAvailabilityIfRequiredAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            bool startupRecoveryRequired = request.IsFull
                && RequiresStartupAvailabilityRecovery(request.Causes)
                && !_availabilityRecoveryCompleted.ContainsKey(syncPair.Id);
            bool lostAvailabilityRecoveryRequired = request.IsFull
                && RequiresLostAvailabilityRecovery(request.Causes);
            if (startupRecoveryRequired || lostAvailabilityRecoveryRequired)
            {
                await RecoverPersistedAvailabilityAsync(syncPair, cancellationToken).ConfigureAwait(false);
                if (startupRecoveryRequired)
                {
                    _availabilityRecoveryCompleted.TryAdd(syncPair.Id, 0);
                }
            }
        }

        private async Task<WindowsVirtualFilesAvailabilityRun> CreateAvailabilityRunAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            HashSet<string> manualDehydrationPathKeys = await FindManualDehydrationPathKeysAsync(
                    syncPair,
                    request.LocalChangedPaths,
                    cancellationToken)
                .ConfigureAwait(false);
            return new WindowsVirtualFilesAvailabilityRun(request, manualDehydrationPathKeys);
        }

        private async Task ProcessAvailabilityPathsAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            CancellationToken cancellationToken)
        {
            try
            {
                foreach (string relativePath in run.Request.LocalChangedPaths
                             .OrderBy(static path => GetAvailabilityPathDepth(path))
                             .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (run.HandledRootAvailability)
                    {
                        continue;
                    }

                    await ProcessAvailabilityPathAsync(syncPair, run, relativePath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                CompleteManualDehydrationProgress(syncPair, run);
            }
        }

        private async Task ProcessAvailabilityPathAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (IsRootRelativePath(relativePath))
            {
                await HandleRootAvailabilityAsync(syncPair, run, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsHandledAvailabilityPath(relativePath, run.HandledAvailabilityPathKeys))
            {
                return;
            }

            bool handledDirectory = await TryHandleDirectoryAvailabilityAsync(
                    syncPair,
                    run,
                    relativePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (handledDirectory)
            {
                return;
            }

            await HandleFileAvailabilityAsync(syncPair, run, relativePath, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task HandleRootAvailabilityAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            CancellationToken cancellationToken)
        {
            if (RequiresStartupAvailabilityRecovery(run.Request.Causes))
            {
                await RecoverRootAvailabilityIfRequiredAsync(syncPair, cancellationToken).ConfigureAwait(false);
                run.HandledRootAvailability = true;
            }
            else
            {
                bool handledRootHydration = await TryHandleManualRootHydrationAsync(
                        syncPair,
                        run.Request,
                        run.HandledAvailabilityPathKeys,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!handledRootHydration)
                {
                    run.RequiresFullPass = run.Request.LocalChangedPaths.Count == 1;
                }
            }
        }

        private async Task RecoverRootAvailabilityIfRequiredAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            if (_availabilityRecoveryCompleted.ContainsKey(syncPair.Id))
            {
                return;
            }

            await RecoverPersistedAvailabilityAsync(syncPair, cancellationToken).ConfigureAwait(false);
            _availabilityRecoveryCompleted.TryAdd(syncPair.Id, 0);
        }

        private async Task<bool> TryHandleDirectoryAvailabilityAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (await TryHandleManualDirectoryHydrationAsync(
                    syncPair,
                    run.Request,
                    relativePath,
                    run.HandledAvailabilityPathKeys,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await TryHandleManualDirectoryUnpinAsync(
                    syncPair,
                    relativePath,
                    run.HandledAvailabilityPathKeys,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return await TryHandleManualDirectoryDehydrationAsync(syncPair, relativePath, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task HandleFileAvailabilityAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string relativePath,
            CancellationToken cancellationToken)
        {
            string? manualDehydrationPath = ResolveManualDehydrationPath(run, relativePath);
            bool hydrated = await TryHandleManualHydrationAsync(
                    syncPair,
                    relativePath,
                    run.Request,
                    cancellationToken)
                .ConfigureAwait(false);
            run.CurrentDehydrationStarted = false;
            Action<string>? onDehydrationStarting = CreateDehydrationStartingCallback(
                syncPair,
                run,
                manualDehydrationPath);
            bool dehydrated = !hydrated
                && await TryHandleManualDehydrationAsync(
                        syncPair,
                        relativePath,
                        onDehydrationStarting,
                        cancellationToken)
                    .ConfigureAwait(false);
            ApplyManualDehydrationOutcome(syncPair, run, manualDehydrationPath, dehydrated);
            if (!hydrated && !dehydrated)
            {
                run.RemainingPaths.Add(relativePath);
            }
        }

        private static string? ResolveManualDehydrationPath(
            WindowsVirtualFilesAvailabilityRun run,
            string relativePath)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return null;
            }

            return run.ManualDehydrationPathKeys.Contains(SyncPath.ToKey(normalizedPath))
                ? normalizedPath
                : null;
        }

        private Action<string>? CreateDehydrationStartingCallback(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string? manualDehydrationPath)
        {
            if (manualDehydrationPath is null)
            {
                return null;
            }

            return currentPath => StartManualDehydrationProgress(syncPair, run, currentPath);
        }

        private void StartManualDehydrationProgress(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string currentPath)
        {
            run.CurrentDehydrationStarted = true;
            if (!run.ManualDehydrationProgressStarted)
            {
                run.ManualDehydrationProgressStarted = true;
                PublishManualDehydrationProgress(syncPair, run, string.Empty, isCompleted: false);
            }

            PublishManualDehydrationProgress(syncPair, run, currentPath, isCompleted: false);
        }

        private void ApplyManualDehydrationOutcome(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string? manualDehydrationPath,
            bool dehydrated)
        {
            if (dehydrated && run.CurrentDehydrationStarted)
            {
                run.CompletedManualDehydrations++;
                PublishManualDehydrationProgress(syncPair, run, manualDehydrationPath!, isCompleted: false);
                return;
            }

            if (manualDehydrationPath is null || run.CurrentDehydrationStarted)
            {
                return;
            }

            run.TotalManualDehydrations--;
            if (run.ManualDehydrationProgressStarted)
            {
                PublishManualDehydrationProgress(syncPair, run, string.Empty, isCompleted: false);
            }
        }

        private void CompleteManualDehydrationProgress(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run)
        {
            if (run.ManualDehydrationProgressStarted)
            {
                PublishManualDehydrationProgress(syncPair, run, string.Empty, isCompleted: true);
            }
        }

        private void PublishManualDehydrationProgress(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            string currentPath,
            bool isCompleted)
        {
            PublishDehydrationProgress(
                syncPair.Id,
                run.Request,
                run.ManualDehydrationStartedAt,
                run.CompletedManualDehydrations,
                run.TotalManualDehydrations,
                currentPath,
                isCompleted);
        }

        private async Task RunRemainingSyncAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            CancellationToken cancellationToken)
        {
            if (run.RemainingPaths.Count == 0)
            {
                await RunWithoutRemainingPathsAsync(syncPair, run, cancellationToken).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<string> remainingPaths = OrderRemainingPaths(run);
            SyncRunRequest remainingRequest = CreateRemainingRequest(run, remainingPaths);
            await _inner.RunOnceAsync(syncPair, remainingRequest, cancellationToken).ConfigureAwait(false);
        }

        private async Task RunWithoutRemainingPathsAsync(
            SyncPairSettings syncPair,
            WindowsVirtualFilesAvailabilityRun run,
            CancellationToken cancellationToken)
        {
            if (!run.Request.IsFull && !run.RequiresFullPass)
            {
                return;
            }

            await _inner
                .RunOnceAsync(syncPair, SyncRunRequest.ForFull(run.Request.Causes), cancellationToken)
                .ConfigureAwait(false);
        }

        private static IReadOnlyList<string> OrderRemainingPaths(WindowsVirtualFilesAvailabilityRun run)
        {
            if (run.RemainingPaths.Count <= 1)
            {
                return run.RemainingPaths;
            }

            HashSet<string> remainingPathSet = new(run.RemainingPaths, StringComparer.OrdinalIgnoreCase);
            return run.Request.LocalChangedPaths.Where(remainingPathSet.Contains).ToArray();
        }

        private static SyncRunRequest CreateRemainingRequest(
            WindowsVirtualFilesAvailabilityRun run,
            IReadOnlyList<string> remainingPaths)
        {
            if (run.Request.IsFull || run.RequiresFullPass)
            {
                return CreateFullRequestWithRemainingPaths(run.Request, remainingPaths);
            }

            if (remainingPaths.Count == run.Request.LocalChangedPaths.Count)
            {
                return run.Request;
            }

            return SyncRunRequest.ForLocalChangedPaths(
                remainingPaths,
                FilterDeletedPaths(run.Request.LocalDeletedPaths, remainingPaths),
                run.Request.Causes);
        }

        private static SyncRunRequest CreateFullRequestWithRemainingPaths(
            SyncRunRequest request,
            IReadOnlyList<string> remainingPaths)
        {
            SyncRunRequest fullRequest = SyncRunRequest.ForFull(request.Causes);
            if (remainingPaths.Count == 0)
            {
                return fullRequest;
            }

            SyncRunRequest scopedRequest = SyncRunRequest.ForLocalChangedPaths(
                remainingPaths,
                FilterDeletedPaths(request.LocalDeletedPaths, remainingPaths),
                request.Causes);
            return fullRequest.Merge(scopedRequest);
        }

        private static IReadOnlyList<string> FilterDeletedPaths(
            IReadOnlyList<string> deletedPaths,
            IReadOnlyList<string> remainingPaths)
        {
            HashSet<string> remainingPathSet = new(remainingPaths, StringComparer.OrdinalIgnoreCase);
            return deletedPaths
                .Where(remainingPathSet.Contains)
                .ToArray();
        }
    }
}
