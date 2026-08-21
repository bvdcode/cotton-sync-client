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
        private async Task<bool> TryHandleManualHydrationAsync(
            SyncPairSettings syncPair,
            string relativePath,
            SyncRunRequest request,
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
            if (!IsManualAlwaysKeepCandidate(diskState.Attributes, state.PlaceholderHydrationState))
            {
                if (IsUnchangedPinnedPlaceholder(syncPair, state, diskState))
                {
                    _diagnostics.Record(
                        "manual-always-keep",
                        "completed",
                        syncPair.Id.ToString("D"),
                        syncPair.LocalRootPath,
                        normalizedPath,
                        "Explorer Always keep on this device was already hydrated for the tracked placeholder.");
                    return true;
                }

                return false;
            }

            DateTime startedAtUtc = DateTime.UtcNow;
            bool hydrationCompleted = false;
            PublishAvailabilityProgress(
                syncPair.Id,
                request,
                startedAtUtc,
                completedFiles: 0,
                totalFiles: 1,
                currentPath: normalizedPath,
                isCompleted: false);
            try
            {
                await HydrateTrackedPlaceholderAsync(
                        syncPair,
                        normalizedPath,
                        fullPath,
                        state,
                        persistState: true,
                        suppressProviderWrite: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                hydrationCompleted = true;
                PublishAvailabilityProgress(
                    syncPair.Id,
                    request,
                    startedAtUtc,
                    completedFiles: 1,
                    totalFiles: 1,
                    currentPath: normalizedPath,
                    isCompleted: false);
            }
            finally
            {
                PublishAvailabilityProgress(
                    syncPair.Id,
                    request,
                    startedAtUtc,
                    completedFiles: hydrationCompleted ? 1 : 0,
                    totalFiles: 1,
                    currentPath: string.Empty,
                    isCompleted: true);
            }

            return true;
        }

        private async Task<(int HydratedFiles, int AlreadyHydratedFiles)> HydrateTrackedAvailabilityFilesAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            IReadOnlyList<SyncStateEntry> subtreeEntries,
            List<SyncStateEntry> hydratedEntries,
            HashSet<string>? handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            SyncStateEntry[] trackedEntries = subtreeEntries
                .Where(static entry => IsTrackedVirtualFile(entry))
                .ToArray();
            IReadOnlyDictionary<string, WindowsVirtualFileDiskState?> initialDiskStates =
                CaptureInitialHydrationStates(syncPair, trackedEntries);
            WindowsVirtualFilesHydrationRun run = new(request, initialDiskStates);
            PublishHydrationRunProgress(syncPair, run, string.Empty, isCompleted: false);
            try
            {
                foreach (SyncStateEntry entry in subtreeEntries)
                {
                    await ProcessHydrationEntryAsync(
                            syncPair,
                            entry,
                            run,
                            hydratedEntries,
                            handledAvailabilityPathKeys,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                PublishHydrationRunProgress(syncPair, run, string.Empty, isCompleted: true);
            }

            return (run.HydratedFiles, run.AlreadyHydratedFiles);
        }

        private IReadOnlyDictionary<string, WindowsVirtualFileDiskState?> CaptureInitialHydrationStates(
            SyncPairSettings syncPair,
            IEnumerable<SyncStateEntry> trackedEntries)
        {
            Dictionary<string, WindowsVirtualFileDiskState?> initialDiskStates = new(StringComparer.OrdinalIgnoreCase);
            foreach (SyncStateEntry entry in trackedEntries)
            {
                string filePath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
                WindowsVirtualFileDiskState? fileState = TryReadDiskState(filePath);
                initialDiskStates[SyncPath.ToKey(entry.RelativePath)] = fileState;
                if (fileState is null || !IsHydrationComplete(fileState.Attributes, entry.PlaceholderHydrationState))
                {
                    _localChangeSuppression?.SuppressProviderWrite(
                        syncPair.Id,
                        syncPair.LocalRootPath,
                        entry.RelativePath);
                }
            }

            return initialDiskStates;
        }

        private async Task ProcessHydrationEntryAsync(
            SyncPairSettings syncPair,
            SyncStateEntry entry,
            WindowsVirtualFilesHydrationRun run,
            ICollection<SyncStateEntry> hydratedEntries,
            ISet<string>? handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTrackedVirtualFile(entry))
            {
                return;
            }

            string filePath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
            WindowsVirtualFileDiskState? fileState = run.InitialDiskStates[SyncPath.ToKey(entry.RelativePath)];
            if (fileState is not null && IsHydrationComplete(fileState.Attributes, entry.PlaceholderHydrationState))
            {
                if (IsUnchangedPinnedPlaceholder(syncPair, entry, fileState))
                {
                    handledAvailabilityPathKeys?.Add(SyncPath.ToKey(entry.RelativePath));
                }

                run.AlreadyHydratedFiles++;
                CompleteHydrationEntry(syncPair, run, entry.RelativePath);
                return;
            }

            PublishHydrationRunProgress(syncPair, run, entry.RelativePath, isCompleted: false);
            if (fileState is null)
            {
                RestoreMissingTrackedPlaceholder(syncPair, entry);
            }

            await HydrateTrackedPlaceholderAsync(
                    syncPair,
                    entry.RelativePath,
                    filePath,
                    entry,
                    persistState: false,
                    suppressProviderWrite: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            hydratedEntries.Add(entry);
            handledAvailabilityPathKeys?.Add(SyncPath.ToKey(entry.RelativePath));
            run.HydratedFiles++;
            CompleteHydrationEntry(syncPair, run, entry.RelativePath);
        }

        private void CompleteHydrationEntry(
            SyncPairSettings syncPair,
            WindowsVirtualFilesHydrationRun run,
            string relativePath)
        {
            run.CompletedFiles++;
            PublishHydrationRunProgress(syncPair, run, relativePath, isCompleted: false);
        }

        private void PublishHydrationRunProgress(
            SyncPairSettings syncPair,
            WindowsVirtualFilesHydrationRun run,
            string currentPath,
            bool isCompleted)
        {
            PublishAvailabilityProgress(
                syncPair.Id,
                run.Request,
                run.StartedAt,
                run.CompletedFiles,
                run.TotalFiles,
                currentPath,
                isCompleted);
        }

        private void RestoreMissingTrackedPlaceholder(
            SyncPairSettings syncPair,
            SyncStateEntry state)
        {
            try
            {
                RemoteFilePlaceholderResult restored =
                    _cloudFiles.RestoreMissingFilePlaceholder(syncPair, state);
                state.PlaceholderIdentity = restored.PlaceholderIdentity;
                _diagnostics.Record(
                    "manual-always-keep-placeholder-repair",
                    "completed",
                    syncPair.Id.ToString("D"),
                    syncPair.LocalRootPath,
                    state.RelativePath,
                    "Restored a tracked placeholder that was missing when Explorer requested offline availability.");
            }
            catch (Exception exception)
            {
                string details = "Missing placeholder recovery failed during Explorer Always keep on this device: "
                    + exception.Message;
                RecordFailed(syncPair, state.RelativePath, details);
                throw;
            }
        }

        private void PublishAvailabilityProgress(
            Guid syncPairId,
            SyncRunRequest request,
            DateTime startedAtUtc,
            int completedFiles,
            int totalFiles,
            string currentPath,
            bool isCompleted)
        {
            if (totalFiles <= 0)
            {
                return;
            }

            _runProgressPublisher?.Publish(new AppRunProgress(
                syncPairId,
                SyncRunProgressStage.HydratingCloudFiles,
                completedFiles,
                totalFiles,
                currentPath,
                startedAtUtc,
                isCompleted,
                DateTime.UtcNow,
                causes: request.Causes,
                isFull: request.IsFull,
                requestedPathCount: request.IsFull ? 0 : request.LocalChangedPaths.Count));
        }

        private void PublishDehydrationProgress(
            Guid syncPairId,
            SyncRunRequest request,
            DateTime startedAtUtc,
            int completedFiles,
            int totalFiles,
            string currentPath,
            bool isCompleted)
        {
            _runProgressPublisher?.Publish(new AppRunProgress(
                syncPairId,
                SyncRunProgressStage.DehydratingCloudFiles,
                completedFiles,
                totalFiles,
                currentPath,
                startedAtUtc,
                isCompleted,
                DateTime.UtcNow,
                causes: request.Causes,
                isFull: request.IsFull,
                requestedPathCount: request.IsFull ? 0 : request.LocalChangedPaths.Count));
        }

        private async Task HydrateTrackedPlaceholderAsync(
            SyncPairSettings syncPair,
            string normalizedPath,
            string fullPath,
            SyncStateEntry state,
            bool persistState,
            bool suppressProviderWrite,
            CancellationToken cancellationToken)
        {
            if (suppressProviderWrite)
            {
                _localChangeSuppression?.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, normalizedPath);
            }
            try
            {
                _cloudFiles.HydratePlaceholder(syncPair, normalizedPath);
            }
            catch (Exception exception)
            {
                RecordFailed(syncPair, normalizedPath, "Explorer Always keep on this device hydration failed: " + exception.Message);
                throw;
            }

            WindowsVirtualFileDiskState? hydratedState = _readDiskState(fullPath);
            if (hydratedState is null)
            {
                const string details = "Hydrated placeholder is missing after Explorer Always keep on this device.";
                RecordFailed(syncPair, normalizedPath, details);
                throw new InvalidOperationException(details);
            }

            if (!SizeMatchesBaseline(state, hydratedState.Length))
            {
                const string details = "Hydrated local size differs from the tracked remote file.";
                RecordFailed(syncPair, normalizedPath, details);
                throw new InvalidOperationException(details);
            }

            if (!await ContentMatchesRemoteAsync(state, normalizedPath, fullPath, hydratedState, cancellationToken)
                    .ConfigureAwait(false))
            {
                const string details = "Hydrated local content differs from the tracked remote file.";
                RecordFailed(syncPair, normalizedPath, details);
                throw new InvalidOperationException(details);
            }

            state.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = state.RemoteContentHash;
            state.LocalLastWriteUtc = hydratedState.LastWriteUtc;
            state.LocalSizeBytes = hydratedState.Length;
            state.SyncedAtUtc = DateTime.UtcNow;
            if (persistState)
            {
                await _stateStore.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
            }

            _diagnostics.Record(
                "manual-always-keep",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Always keep on this device hydrated the tracked placeholder.");
        }
    }
}
