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
    internal class WindowsVirtualFilesDehydrationPairWork : ISyncPairWork
    {
        private const int AvailabilityStateWriteBatchSize = 128;
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        private readonly ISyncPairWork _inner;
        private readonly ISyncStateStore _stateStore;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalFileContentHasher _contentHasher;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly IAppRunProgressPublisher? _runProgressPublisher;
        private readonly Func<string, WindowsVirtualFileDiskState?> _readDiskState;
        private readonly ConcurrentDictionary<Guid, byte> _availabilityRecoveryCompleted = new();

        public WindowsVirtualFilesDehydrationPairWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalFileContentHasher? contentHasher = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, WindowsVirtualFileDiskState?>? readDiskState = null,
            ILocalChangeSuppression? localChangeSuppression = null,
            IAppRunProgressPublisher? runProgressPublisher = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _contentHasher = contentHasher ?? new LocalFileScanner();
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _localChangeSuppression = localChangeSuppression;
            _runProgressPublisher = runProgressPublisher;
            _readDiskState = readDiskState ?? ReadDiskState;
        }

        public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            return _inner.RunOnceAsync(syncPair, cancellationToken);
        }

        public async Task RunOnceAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                await _inner.RunOnceAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
                return;
            }

            await RecoverAvailabilityIfRequiredAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
            WindowsVirtualFilesAvailabilityRun run = await CreateAvailabilityRunAsync(
                    syncPair,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            await ProcessAvailabilityPathsAsync(syncPair, run, cancellationToken).ConfigureAwait(false);
            await RunRemainingSyncAsync(syncPair, run, cancellationToken).ConfigureAwait(false);
        }

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

        private async Task<HashSet<string>> FindManualDehydrationPathKeysAsync(
            SyncPairSettings syncPair,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken)
        {
            List<(string PathKey, bool IsDirectory, FileAttributes Attributes)> snapshots = [];
            foreach (string relativePath in relativePaths)
            {
                (string PathKey, bool IsDirectory, FileAttributes Attributes)? snapshot =
                    await TryReadAvailabilitySnapshotAsync(syncPair, relativePath, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.HasValue)
                {
                    snapshots.Add(snapshot.Value);
                }
            }

            string[] neutralDirectoryKeys = snapshots
                .Where(static snapshot => IsNeutralAvailabilityDirectorySnapshot(snapshot))
                .Select(static snapshot => snapshot.PathKey)
                .ToArray();
            return snapshots
                .Where(static snapshot => IsManualDehydrationFileSnapshot(snapshot))
                .Where(snapshot => !IsInsideAnyDirectory(snapshot.PathKey, neutralDirectoryKeys))
                .Select(static snapshot => snapshot.PathKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<(string PathKey, bool IsDirectory, FileAttributes Attributes)?>
            TryReadAvailabilitySnapshotAsync(
                SyncPairSettings syncPair,
                string relativePath,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRootRelativePath(relativePath)
                || !TryNormalizePath(relativePath, out string normalizedPath))
            {
                return null;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            bool isDirectory = IsTrackedVirtualDirectory(state);
            if (!isDirectory && !IsTrackedVirtualFile(state))
            {
                return null;
            }

            string? fullPath = TryResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            WindowsVirtualFileDiskState? diskState = fullPath is null ? null : TryReadDiskState(fullPath);
            return diskState is null
                ? null
                : (SyncPath.ToKey(normalizedPath), isDirectory, diskState.Attributes);
        }

        private static bool IsNeutralAvailabilityDirectorySnapshot(
            (string PathKey, bool IsDirectory, FileAttributes Attributes) snapshot)
        {
            return snapshot.IsDirectory && IsManualPinRemovalDirectoryCandidate(snapshot.Attributes);
        }

        private static bool IsManualDehydrationFileSnapshot(
            (string PathKey, bool IsDirectory, FileAttributes Attributes) snapshot)
        {
            return !snapshot.IsDirectory
                && (IsManualFreeUpSpaceCandidate(snapshot.Attributes)
                    || IsCompletedManualFreeUpSpaceCandidate(snapshot.Attributes));
        }

        private static bool IsInsideAnyDirectory(string pathKey, IReadOnlyList<string> directoryKeys)
        {
            return directoryKeys.Any(directoryKey => IsSameOrDescendantPathKey(pathKey, directoryKey));
        }

        private static bool IsSameOrDescendantPathKey(string pathKey, string directoryKey)
        {
            return string.Equals(pathKey, directoryKey, StringComparison.OrdinalIgnoreCase)
                || pathKey.StartsWith(directoryKey.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        private async Task RecoverPersistedAvailabilityAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            List<SyncStateEntry> hydratedEntries = new(AvailabilityStateWriteBatchSize);
            Dictionary<string, SyncStateEntry> directoryEntries = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> completedDirectoryKeys = new(StringComparer.OrdinalIgnoreCase);
            int hydratedFiles = 0;
            int alreadyHydratedFiles = 0;

            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                WindowsVirtualFilesAvailabilityRecoveryOutcome outcome;
                try
                {
                    outcome = await RecoverPersistedAvailabilityEntryAsync(
                                syncPair,
                                entry,
                                hydratedEntries,
                                directoryEntries,
                                completedDirectoryKeys,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRecoverableAvailabilityFailure(exception))
                {
                    RecordAvailabilityRecoverySkipped(syncPair, entry.RelativePath, exception);
                    continue;
                }
                switch (outcome)
                {
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored:
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.DirectoryTracked:
                        break;
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.AlreadyHydrated:
                        alreadyHydratedFiles++;
                        break;
                    case WindowsVirtualFilesAvailabilityRecoveryOutcome.Hydrated:
                        hydratedFiles++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported recovery outcome.");
                }

                if (hydratedEntries.Count >= AvailabilityStateWriteBatchSize)
                {
                    await FlushAvailabilityStateAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
                }
            }

            await FlushAvailabilityStateAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            int completedDirectories = CompleteRecoveredDirectories(
                syncPair,
                directoryEntries,
                completedDirectoryKeys,
                cancellationToken);
            RecordAvailabilityRecoveryCompleted(
                syncPair,
                hydratedFiles,
                alreadyHydratedFiles,
                completedDirectories);
        }

        private async Task<WindowsVirtualFilesAvailabilityRecoveryOutcome> RecoverPersistedAvailabilityEntryAsync(
            SyncPairSettings syncPair,
            SyncStateEntry entry,
            ICollection<SyncStateEntry> hydratedEntries,
            IDictionary<string, SyncStateEntry> directoryEntries,
            ISet<string> completedDirectoryKeys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            if (entry.Kind == SyncEntryKind.Directory)
            {
                directoryEntries[SyncPath.ToKey(entry.RelativePath)] = entry;
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.DirectoryTracked;
            }

            if (!IsTrackedVirtualFile(entry))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            string filePath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
            WindowsVirtualFileDiskState? fileState = TryReadDiskState(filePath);
            if (fileState is null || !HasRawAttribute(fileState.Attributes, FileAttributePinned))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            if (IsHydrationComplete(fileState.Attributes, entry.PlaceholderHydrationState))
            {
                AddAncestorDirectoryKeys(entry.RelativePath, completedDirectoryKeys);
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.AlreadyHydrated;
            }

            if (!IsManualAlwaysKeepCandidate(fileState.Attributes, entry.PlaceholderHydrationState))
            {
                return WindowsVirtualFilesAvailabilityRecoveryOutcome.Ignored;
            }

            await HydrateTrackedPlaceholderAsync(
                    syncPair,
                    entry.RelativePath,
                    filePath,
                    entry,
                    persistState: false,
                    suppressProviderWrite: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            hydratedEntries.Add(entry);
            AddAncestorDirectoryKeys(entry.RelativePath, completedDirectoryKeys);
            return WindowsVirtualFilesAvailabilityRecoveryOutcome.Hydrated;
        }

        private async Task FlushAvailabilityStateAsync(
            List<SyncStateEntry> hydratedEntries,
            CancellationToken cancellationToken)
        {
            if (hydratedEntries.Count == 0)
            {
                return;
            }

            await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            hydratedEntries.Clear();
        }

        private int CompleteRecoveredDirectories(
            SyncPairSettings syncPair,
            IReadOnlyDictionary<string, SyncStateEntry> directoryEntries,
            IEnumerable<string> completedDirectoryKeys,
            CancellationToken cancellationToken)
        {
            SyncStateEntry[] completedDirectories = completedDirectoryKeys
                .Select(key => directoryEntries.GetValueOrDefault(key))
                .OfType<SyncStateEntry>()
                .OrderByDescending(static entry => GetPathDepth(entry.RelativePath))
                .ToArray();
            foreach (SyncStateEntry entry in completedDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _cloudFiles.SetInSyncState(syncPair, entry.RelativePath);
            }

            return completedDirectories.Length;
        }

        private void RecordAvailabilityRecoveryCompleted(
            SyncPairSettings syncPair,
            int hydratedFiles,
            int alreadyHydratedFiles,
            int completedDirectories)
        {
            _diagnostics.Record(
                "manual-always-keep-recovery",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                ".",
                "Hydrated "
                + hydratedFiles
                + " persisted pinned files; "
                + alreadyHydratedFiles
                + " were already available; completed "
                + completedDirectories
                + " tracked directories.");
        }

        private void RecordAvailabilityRecoverySkipped(
            SyncPairSettings syncPair,
            string relativePath,
            Exception exception)
        {
            _diagnostics.Record(
                "manual-always-keep-recovery",
                "skipped",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                relativePath,
                "Persisted availability recovery yielded to the primary sync: " + exception.Message,
                exception.HResult);
        }

        private static bool IsRecoverableAvailabilityFailure(Exception exception)
        {
            return exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or WindowsCloudFilesNativeException;
        }

        private async Task<bool> TryHandleManualRootHydrationAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            HashSet<string> handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(syncPair.LocalRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            if (diskState is null || !IsManualAlwaysKeepDirectoryCandidate(diskState.Attributes))
            {
                return false;
            }

            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            var subtreeEntries = new List<SyncStateEntry>();
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    subtreeEntries.Add(entry);
                }
            }

            List<SyncStateEntry> hydratedEntries = new();
            (int hydratedFiles, int alreadyHydratedFiles) = await HydrateTrackedAvailabilityFilesAsync(
                    syncPair,
                    request,
                    subtreeEntries,
                    hydratedEntries,
                    handledAvailabilityPathKeys,
                    cancellationToken)
                .ConfigureAwait(false);

            await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            SyncStateEntry[] directoryEntries = subtreeEntries
                .Where(static entry => entry.Kind == SyncEntryKind.Directory)
                .OrderByDescending(static entry => GetPathDepth(entry.RelativePath))
                .ToArray();
            foreach (SyncStateEntry entry in directoryEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PinDirectoryIfNeeded(syncPair, entry.RelativePath);
                _cloudFiles.SetInSyncState(syncPair, entry.RelativePath);
            }

            _diagnostics.Record(
                "manual-always-keep-root",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                ".",
                "Hydrated "
                + hydratedFiles
                + " tracked files; "
                + alreadyHydratedFiles
                + " were already available; completed "
                + directoryEntries.Length
                + " tracked directories.");
            return true;
        }

        private async Task<bool> TryHandleManualDirectoryHydrationAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            string relativePath,
            HashSet<string> handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            (string NormalizedPath, WindowsVirtualFileDiskState DiskState)? context =
                await TryResolveTrackedVirtualDirectoryAsync(syncPair, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!context.HasValue
                || !IsManualAlwaysKeepDirectoryCandidate(context.Value.DiskState.Attributes))
            {
                return false;
            }

            string normalizedPath = context.Value.NormalizedPath;
            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            IReadOnlyList<SyncStateEntry> subtreeEntries = await LoadDirectorySubtreeEntriesAsync(
                    syncPair,
                    normalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            List<SyncStateEntry> hydratedEntries = new();
            (int hydratedFiles, int alreadyHydratedFiles) = await HydrateTrackedAvailabilityFilesAsync(
                    syncPair,
                    request,
                    subtreeEntries,
                    hydratedEntries,
                    handledAvailabilityPathKeys,
                    cancellationToken)
                .ConfigureAwait(false);

            await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            int completedDirectories = CompleteHydratedDirectories(syncPair, subtreeEntries, cancellationToken);
            handledAvailabilityPathKeys.Add(SyncPath.ToKey(normalizedPath));
            RecordDirectoryHydrationCompleted(
                syncPair,
                normalizedPath,
                hydratedFiles,
                alreadyHydratedFiles,
                completedDirectories);
            return true;
        }

        private async Task<(string NormalizedPath, WindowsVirtualFileDiskState DiskState)?>
            TryResolveTrackedVirtualDirectoryAsync(
                SyncPairSettings syncPair,
                string relativePath,
                CancellationToken cancellationToken)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return null;
            }

            SyncStateEntry? directoryState = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualDirectory(directoryState))
            {
                return null;
            }

            string? fullPath = TryResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            WindowsVirtualFileDiskState? diskState = fullPath is null ? null : TryReadDiskState(fullPath);
            return diskState is null ? null : (normalizedPath, diskState);
        }

        private async Task<IReadOnlyList<SyncStateEntry>> LoadDirectorySubtreeEntriesAsync(
            SyncPairSettings syncPair,
            string normalizedPath,
            CancellationToken cancellationToken)
        {
            List<SyncStateEntry> subtreeEntries = [];
            await foreach (SyncStateEntry entry in _stateStore
                               .LoadEntriesByPathPrefixAsync(
                                   syncPair.Id.ToString("D"),
                                   normalizedPath,
                                   cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                subtreeEntries.Add(entry);
            }

            return subtreeEntries;
        }

        private int CompleteHydratedDirectories(
            SyncPairSettings syncPair,
            IEnumerable<SyncStateEntry> subtreeEntries,
            CancellationToken cancellationToken)
        {
            SyncStateEntry[] directoryEntries = subtreeEntries
                .Where(static entry => entry.Kind == SyncEntryKind.Directory)
                .OrderByDescending(static entry => GetPathDepth(entry.RelativePath))
                .ToArray();
            foreach (SyncStateEntry entry in directoryEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PinDirectoryIfNeeded(syncPair, entry.RelativePath);
                _cloudFiles.SetInSyncState(syncPair, entry.RelativePath);
            }

            return directoryEntries.Length;
        }

        private void RecordDirectoryHydrationCompleted(
            SyncPairSettings syncPair,
            string normalizedPath,
            int hydratedFiles,
            int alreadyHydratedFiles,
            int completedDirectories)
        {
            _diagnostics.Record(
                "manual-always-keep-directory",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Hydrated "
                + hydratedFiles
                + " tracked files; "
                + alreadyHydratedFiles
                + " were already available; completed "
                + completedDirectories
                + " tracked directories.");
        }

        private void PinDirectoryIfNeeded(SyncPairSettings syncPair, string relativePath)
        {
            string fullPath = ResolveFullPath(syncPair.LocalRootPath, relativePath);
            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            if (diskState is not null && IsManualAlwaysKeepDirectoryCandidate(diskState.Attributes))
            {
                return;
            }

            _localChangeSuppression?.SuppressProviderWrite(
                syncPair.Id,
                syncPair.LocalRootPath,
                relativePath);
            _cloudFiles.PinPlaceholder(syncPair, relativePath);
        }

        private async Task<bool> TryHandleManualDirectoryUnpinAsync(
            SyncPairSettings syncPair,
            string relativePath,
            HashSet<string> handledAvailabilityPathKeys,
            CancellationToken cancellationToken)
        {
            (string NormalizedPath, WindowsVirtualFileDiskState DiskState)? context =
                await TryResolveTrackedVirtualDirectoryAsync(syncPair, relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!context.HasValue
                || !IsManualPinRemovalDirectoryCandidate(context.Value.DiskState.Attributes))
            {
                return false;
            }

            string normalizedPath = context.Value.NormalizedPath;
            IReadOnlyList<SyncStateEntry> subtreeEntries = await LoadDirectorySubtreeEntriesAsync(
                    syncPair,
                    normalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CanApplyDirectoryUnpin(syncPair, subtreeEntries, cancellationToken))
            {
                return false;
            }

            foreach (SyncStateEntry entry in subtreeEntries)
            {
                handledAvailabilityPathKeys.Add(SyncPath.ToKey(entry.RelativePath));
            }

            _diagnostics.Record(
                "manual-always-keep-directory",
                "unpinned",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer removed Always keep on this device without changing materialized file content.");
            return true;
        }

        private bool CanApplyDirectoryUnpin(
            SyncPairSettings syncPair,
            IEnumerable<SyncStateEntry> subtreeEntries,
            CancellationToken cancellationToken)
        {
            foreach (SyncStateEntry entry in subtreeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryPath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
                WindowsVirtualFileDiskState? diskState = TryReadDiskState(entryPath);
                if (!IsValidDirectoryUnpinEntry(entry, diskState))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidDirectoryUnpinEntry(
            SyncStateEntry entry,
            WindowsVirtualFileDiskState? diskState)
        {
            if (diskState is null)
            {
                return false;
            }

            if (entry.Kind == SyncEntryKind.Directory)
            {
                return IsManualPinRemovalDirectoryCandidate(diskState.Attributes);
            }

            return IsTrackedVirtualFile(entry)
                && IsManualPinRemovalFileCandidate(diskState.Attributes)
                && MaterializedBaselineMatches(entry, diskState);
        }

        private async Task<bool> TryHandleManualDirectoryDehydrationAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualDirectory(state))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = ResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
            if (diskState is null || !IsManualFreeUpSpaceDirectoryCandidate(diskState.Attributes))
            {
                return false;
            }

            _diagnostics.Record(
                "manual-free-up-space-directory",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Free up space unpinned the tracked directory placeholder.");
            return true;
        }

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

        private static bool IsTrackedVirtualFile(SyncStateEntry? state)
        {
            return state is
            {
                Kind: SyncEntryKind.File,
                PlaceholderIdentity.Length: > 0,
            };
        }

        private static bool IsTrackedVirtualDirectory(SyncStateEntry? state)
        {
            return state is
            {
                Kind: SyncEntryKind.Directory,
                RemoteNodeId: not null,
            };
        }

        private static bool SizeMatchesBaseline(SyncStateEntry state, long localLength)
        {
            long? expectedLength = state.RemoteSizeBytes ?? state.LocalSizeBytes;
            return !expectedLength.HasValue || expectedLength.Value == localLength;
        }

        private static bool MaterializedBaselineMatches(
            SyncStateEntry state,
            WindowsVirtualFileDiskState diskState)
        {
            return state.PlaceholderHydrationState == SyncPlaceholderHydrationState.Hydrated
                && state.LocalSizeBytes == diskState.Length
                && state.LocalLastWriteUtc == diskState.LastWriteUtc
                && !string.IsNullOrWhiteSpace(state.LocalContentHash)
                && string.Equals(
                    state.LocalContentHash,
                    state.RemoteContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool IsUnchangedPinnedPlaceholder(
            SyncPairSettings syncPair,
            SyncStateEntry state,
            WindowsVirtualFileDiskState diskState)
        {
            if (!HasRawAttribute(diskState.Attributes, FileAttributePinned)
                || !MaterializedBaselineMatches(state, diskState))
            {
                return false;
            }

            WindowsCloudFilesPlaceholderState placeholderState = _cloudFiles.GetPlaceholderState(
                syncPair,
                state.RelativePath);
            return placeholderState.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder)
                && placeholderState.HasFlag(WindowsCloudFilesPlaceholderState.InSync);
        }

        private static bool IsManualFreeUpSpaceCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsCompletedManualFreeUpSpaceCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && (HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                    || (attributes & FileAttributes.Offline) != 0);
        }

        private static bool IsManualFreeUpSpaceDirectoryCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributePinned);
        }

        private static bool IsManualPinRemovalDirectoryCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && !HasRawAttribute(attributes, FileAttributePinned)
                && !HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsManualPinRemovalFileCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) == 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && !HasRawAttribute(attributes, FileAttributePinned)
                && !HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsCompletedOnDemandHydrationCandidate(
            SyncStateEntry state,
            FileAttributes attributes)
        {
            return (state.PlaceholderHydrationState is SyncPlaceholderHydrationState.RemoteOnly
                    or SyncPlaceholderHydrationState.Dehydrated)
                && IsManualPinRemovalFileCandidate(attributes);
        }

        private static bool IsManualAlwaysKeepCandidate(
            FileAttributes attributes,
            SyncPlaceholderHydrationState hydrationState)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributePinned)
                && (hydrationState != SyncPlaceholderHydrationState.Hydrated
                    || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                    || (attributes & FileAttributes.Offline) != 0);
        }

        private static bool IsManualAlwaysKeepDirectoryCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributePinned);
        }

        private static bool IsHydrationComplete(
            FileAttributes attributes,
            SyncPlaceholderHydrationState hydrationState)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && hydrationState == SyncPlaceholderHydrationState.Hydrated
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool RequiresStartupAvailabilityRecovery(SyncRunCause causes)
        {
            return (causes & (SyncRunCause.Periodic | SyncRunCause.Resume)) != SyncRunCause.None;
        }

        private static bool RequiresLostAvailabilityRecovery(SyncRunCause causes)
        {
            return (causes & (SyncRunCause.LocalChangeOverflow | SyncRunCause.LocalWatcherError)) != SyncRunCause.None;
        }

        private static void AddAncestorDirectoryKeys(string relativePath, ISet<string> directoryKeys)
        {
            string ancestorPath = SyncPath.Normalize(relativePath);
            int separatorIndex = ancestorPath.LastIndexOf('/');
            while (separatorIndex > 0)
            {
                ancestorPath = ancestorPath[..separatorIndex];
                directoryKeys.Add(SyncPath.ToKey(ancestorPath));
                separatorIndex = ancestorPath.LastIndexOf('/');
            }
        }

        private static bool HasRawAttribute(FileAttributes attributes, int attribute)
        {
            return (((int)attributes) & attribute) == attribute;
        }

        private static bool IsHandledAvailabilityPath(
            string relativePath,
            IReadOnlySet<string> handledAvailabilityPathKeys)
        {
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            return handledAvailabilityPathKeys.Contains(SyncPath.ToKey(normalizedPath));
        }

        private static bool IsRootRelativePath(string relativePath)
        {
            string trimmed = relativePath.Trim();
            return trimmed == "." || trimmed == "/" || trimmed == "\\";
        }

        private static bool TryNormalizePath(string relativePath, out string normalizedPath)
        {
            try
            {
                normalizedPath = SyncPath.Normalize(relativePath);
                return true;
            }
            catch (ArgumentException)
            {
                normalizedPath = string.Empty;
                return false;
            }
        }

        private WindowsVirtualFileDiskState? TryReadDiskState(string fullPath)
        {
            try
            {
                return _readDiskState(fullPath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static int GetPathDepth(string relativePath)
        {
            return relativePath.Count(static character => character == '/');
        }

        private static int GetAvailabilityPathDepth(string relativePath)
        {
            if (IsRootRelativePath(relativePath))
            {
                return -1;
            }

            return relativePath.Count(static character => character is '/' or '\\');
        }

        private static string ResolveFullPath(string localRootPath, string normalizedRelativePath)
        {
            string root = Path.GetFullPath(localRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(
                root,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Virtual file path escaped the sync root.", nameof(normalizedRelativePath));
            }

            return fullPath;
        }

        private static string? TryResolveFullPath(string localRootPath, string normalizedRelativePath)
        {
            try
            {
                return ResolveFullPath(localRootPath, normalizedRelativePath);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static WindowsVirtualFileDiskState? ReadDiskState(string fullPath)
        {
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                file.Refresh();
                return new WindowsVirtualFileDiskState(file.Attributes, file.Length, file.LastWriteTimeUtc);
            }

            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                directory.Refresh();
                return new WindowsVirtualFileDiskState(directory.Attributes, 0, directory.LastWriteTimeUtc);
            }

            return null;
        }
    }
}
