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

            HashSet<string> manualDehydrationPathKeys = await FindManualDehydrationPathKeysAsync(
                    syncPair,
                    request.LocalChangedPaths,
                    cancellationToken)
                .ConfigureAwait(false);
            int completedManualDehydrations = 0;
            int totalManualDehydrations = manualDehydrationPathKeys.Count;
            bool manualDehydrationProgressStarted = false;
            DateTime manualDehydrationStartedAtUtc = DateTime.UtcNow;
            List<string> remainingPaths = [];
            var handledAvailabilityPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool handledRootAvailability = false;
            bool requiresFullPass = false;
            try
            {
                foreach (string relativePath in request.LocalChangedPaths
                             .OrderBy(static path => GetAvailabilityPathDepth(path))
                             .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handledRootAvailability)
                    {
                        continue;
                    }

                    if (IsRootRelativePath(relativePath))
                    {
                        if (RequiresStartupAvailabilityRecovery(request.Causes))
                        {
                            if (!_availabilityRecoveryCompleted.ContainsKey(syncPair.Id))
                            {
                                await RecoverPersistedAvailabilityAsync(syncPair, cancellationToken).ConfigureAwait(false);
                                _availabilityRecoveryCompleted.TryAdd(syncPair.Id, 0);
                            }

                            handledRootAvailability = true;
                        }
                        else
                        {
                            handledRootAvailability = await TryHandleManualRootHydrationAsync(syncPair, request, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (!handledRootAvailability)
                        {
                            requiresFullPass = request.LocalChangedPaths.Count == 1;
                        }

                        continue;
                    }

                    if (IsHandledAvailabilityPath(relativePath, handledAvailabilityPathKeys))
                    {
                        continue;
                    }

                    bool hydratedDirectory = await TryHandleManualDirectoryHydrationAsync(
                            syncPair,
                            request,
                            relativePath,
                            handledAvailabilityPathKeys,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (hydratedDirectory)
                    {
                        continue;
                    }

                    if (await TryHandleManualDirectoryUnpinAsync(
                                syncPair,
                                relativePath,
                                handledAvailabilityPathKeys,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (await TryHandleManualDirectoryDehydrationAsync(syncPair, relativePath, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        continue;
                    }

                    string? normalizedManualDehydrationPath = TryNormalizePath(relativePath, out string normalizedPath)
                        && manualDehydrationPathKeys.Contains(SyncPath.ToKey(normalizedPath))
                            ? normalizedPath
                            : null;
                    bool hydratedFile = await TryHandleManualHydrationAsync(
                            syncPair,
                            relativePath,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);
                    bool currentDehydrationStarted = false;
                    Action<string>? onDehydrationStarting = normalizedManualDehydrationPath is null
                        ? null
                        : currentPath =>
                        {
                            currentDehydrationStarted = true;
                            if (!manualDehydrationProgressStarted)
                            {
                                manualDehydrationProgressStarted = true;
                                PublishDehydrationProgress(
                                    syncPair.Id,
                                    request,
                                    manualDehydrationStartedAtUtc,
                                    completedManualDehydrations,
                                    totalManualDehydrations,
                                    currentPath: string.Empty,
                                    isCompleted: false);
                            }

                            PublishDehydrationProgress(
                                syncPair.Id,
                                request,
                                manualDehydrationStartedAtUtc,
                                completedManualDehydrations,
                                totalManualDehydrations,
                                currentPath,
                                isCompleted: false);
                        };
                    bool dehydratedFile = !hydratedFile
                        && await TryHandleManualDehydrationAsync(
                                syncPair,
                                relativePath,
                                onDehydrationStarting,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (dehydratedFile && currentDehydrationStarted)
                    {
                        completedManualDehydrations++;
                        PublishDehydrationProgress(
                            syncPair.Id,
                            request,
                            manualDehydrationStartedAtUtc,
                            completedManualDehydrations,
                            totalManualDehydrations,
                            normalizedManualDehydrationPath!,
                            isCompleted: false);
                    }
                    else if (normalizedManualDehydrationPath is not null && !currentDehydrationStarted)
                    {
                        totalManualDehydrations--;
                        if (manualDehydrationProgressStarted)
                        {
                            PublishDehydrationProgress(
                                syncPair.Id,
                                request,
                                manualDehydrationStartedAtUtc,
                                completedManualDehydrations,
                                totalManualDehydrations,
                                currentPath: string.Empty,
                                isCompleted: false);
                        }
                    }

                    if (!hydratedFile && !dehydratedFile)
                    {
                        remainingPaths.Add(relativePath);
                    }
                }
            }
            finally
            {
                if (manualDehydrationProgressStarted)
                {
                    PublishDehydrationProgress(
                        syncPair.Id,
                        request,
                        manualDehydrationStartedAtUtc,
                        completedManualDehydrations,
                        totalManualDehydrations,
                        currentPath: string.Empty,
                        isCompleted: true);
                }
            }

            if (remainingPaths.Count == 0)
            {
                if (!request.IsFull && !requiresFullPass)
                {
                    return;
                }

                await _inner
                    .RunOnceAsync(syncPair, SyncRunRequest.ForFull(request.Causes), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (remainingPaths.Count > 1)
            {
                HashSet<string> remainingPathSet = new(remainingPaths, StringComparer.OrdinalIgnoreCase);
                remainingPaths = request.LocalChangedPaths
                    .Where(remainingPathSet.Contains)
                    .ToList();
            }

            SyncRunRequest remainingRequest = request.IsFull || requiresFullPass
                ? CreateFullRequestWithRemainingPaths(request, remainingPaths)
                : remainingPaths.Count == request.LocalChangedPaths.Count
                    ? request
                    : SyncRunRequest.ForLocalChangedPaths(
                        remainingPaths,
                        FilterDeletedPaths(request.LocalDeletedPaths, remainingPaths),
                        request.Causes);
            await _inner.RunOnceAsync(syncPair, remainingRequest, cancellationToken).ConfigureAwait(false);
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
            var snapshots = new List<(string PathKey, bool IsDirectory, FileAttributes Attributes)>();
            foreach (string relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRootRelativePath(relativePath)
                    || !TryNormalizePath(relativePath, out string normalizedPath))
                {
                    continue;
                }

                SyncStateEntry? state = await _stateStore
                    .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                    .ConfigureAwait(false);
                bool isDirectory = IsTrackedVirtualDirectory(state);
                if (!isDirectory && !IsTrackedVirtualFile(state))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = ResolveFullPath(syncPair.LocalRootPath, normalizedPath);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                catch (NotSupportedException)
                {
                    continue;
                }

                WindowsVirtualFileDiskState? diskState = TryReadDiskState(fullPath);
                if (diskState is not null)
                {
                    snapshots.Add((SyncPath.ToKey(normalizedPath), isDirectory, diskState.Attributes));
                }
            }

            string[] neutralDirectoryKeys = snapshots
                .Where(static snapshot => snapshot.IsDirectory
                    && IsManualPinRemovalDirectoryCandidate(snapshot.Attributes))
                .Select(static snapshot => snapshot.PathKey)
                .ToArray();
            return snapshots
                .Where(static snapshot => !snapshot.IsDirectory
                    && (IsManualFreeUpSpaceCandidate(snapshot.Attributes)
                        || IsCompletedManualFreeUpSpaceCandidate(snapshot.Attributes)))
                .Where(snapshot => neutralDirectoryKeys.All(directoryKey =>
                    !IsSameOrDescendantPathKey(snapshot.PathKey, directoryKey)))
                .Select(static snapshot => snapshot.PathKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            var hydratedEntries = new List<SyncStateEntry>(AvailabilityStateWriteBatchSize);
            var directoryEntries = new Dictionary<string, SyncStateEntry>(StringComparer.OrdinalIgnoreCase);
            var completedDirectoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int hydratedFiles = 0;
            int alreadyHydratedFiles = 0;

            await foreach (SyncStateEntry entry in _stateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyncPathIgnoreRules.ShouldIgnore(entry.RelativePath))
                {
                    continue;
                }

                if (entry.Kind == SyncEntryKind.Directory)
                {
                    directoryEntries[SyncPath.ToKey(entry.RelativePath)] = entry;
                    continue;
                }

                if (!IsTrackedVirtualFile(entry))
                {
                    continue;
                }

                string filePath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
                WindowsVirtualFileDiskState? fileState = TryReadDiskState(filePath);
                if (fileState is null || !HasRawAttribute(fileState.Attributes, FileAttributePinned))
                {
                    continue;
                }

                if (IsHydrationComplete(fileState.Attributes, entry.PlaceholderHydrationState))
                {
                    alreadyHydratedFiles++;
                    AddAncestorDirectoryKeys(entry.RelativePath, completedDirectoryKeys);
                    continue;
                }

                if (!IsManualAlwaysKeepCandidate(fileState.Attributes, entry.PlaceholderHydrationState))
                {
                    continue;
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
                hydratedFiles++;
                AddAncestorDirectoryKeys(entry.RelativePath, completedDirectoryKeys);
                if (hydratedEntries.Count >= AvailabilityStateWriteBatchSize)
                {
                    await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
                    hydratedEntries.Clear();
                }
            }

            if (hydratedEntries.Count > 0)
            {
                await _stateStore.UpsertManyAsync(hydratedEntries, cancellationToken).ConfigureAwait(false);
            }

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
                + completedDirectories.Length
                + " tracked directories.");
        }

        private async Task<bool> TryHandleManualRootHydrationAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
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
                    handledAvailabilityPathKeys: null,
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
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            SyncStateEntry? directoryState = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualDirectory(directoryState))
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
            if (diskState is null || !IsManualAlwaysKeepDirectoryCandidate(diskState.Attributes))
            {
                return false;
            }

            using IDisposable? providerWriteBurst = _localChangeSuppression?
                .SuppressProviderWriteBurst(syncPair.Id, syncPair.LocalRootPath);
            var subtreeEntries = new List<SyncStateEntry>();
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

            handledAvailabilityPathKeys.Add(SyncPath.ToKey(normalizedPath));
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
                + directoryEntries.Length
                + " tracked directories.");
            return true;
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
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            SyncStateEntry? directoryState = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualDirectory(directoryState))
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
            if (diskState is null || !IsManualPinRemovalDirectoryCandidate(diskState.Attributes))
            {
                return false;
            }

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

            foreach (SyncStateEntry entry in subtreeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryPath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
                WindowsVirtualFileDiskState? entryDiskState = TryReadDiskState(entryPath);
                if (entryDiskState is null)
                {
                    return false;
                }

                if (entry.Kind == SyncEntryKind.Directory)
                {
                    if (!IsManualPinRemovalDirectoryCandidate(entryDiskState.Attributes))
                    {
                        return false;
                    }

                    continue;
                }

                if (!IsTrackedVirtualFile(entry)
                    || !IsManualPinRemovalFileCandidate(entryDiskState.Attributes)
                    || !MaterializedBaselineMatches(entry, entryDiskState))
                {
                    return false;
                }
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
            if (!TryNormalizePath(relativePath, out string normalizedPath))
            {
                return false;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualFile(state))
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

            WindowsVirtualFileDiskState? diskState;
            try
            {
                diskState = _readDiskState(fullPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (diskState is null || !IsManualAlwaysKeepCandidate(diskState.Attributes, state!.PlaceholderHydrationState))
            {
                if (diskState is not null
                    && HasRawAttribute(diskState.Attributes, FileAttributePinned)
                    && IsHydrationComplete(diskState.Attributes, state!.PlaceholderHydrationState))
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
                        state!,
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

            int hydratedFiles = 0;
            int alreadyHydratedFiles = 0;
            int totalFiles = trackedEntries.Length;
            int completedFiles = 0;
            DateTime startedAtUtc = DateTime.UtcNow;
            PublishAvailabilityProgress(
                syncPair.Id,
                request,
                startedAtUtc,
                completedFiles,
                totalFiles,
                currentPath: string.Empty,
                isCompleted: false);
            try
            {
                foreach (SyncStateEntry entry in subtreeEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    handledAvailabilityPathKeys?.Add(SyncPath.ToKey(entry.RelativePath));
                    if (!IsTrackedVirtualFile(entry))
                    {
                        continue;
                    }

                    string filePath = ResolveFullPath(syncPair.LocalRootPath, entry.RelativePath);
                    WindowsVirtualFileDiskState? fileState = initialDiskStates[SyncPath.ToKey(entry.RelativePath)];
                    if (fileState is not null && IsHydrationComplete(fileState.Attributes, entry.PlaceholderHydrationState))
                    {
                        alreadyHydratedFiles++;
                        completedFiles++;
                        PublishAvailabilityProgress(
                            syncPair.Id,
                            request,
                            startedAtUtc,
                            completedFiles,
                            totalFiles,
                            entry.RelativePath,
                            isCompleted: false);
                        continue;
                    }

                    PublishAvailabilityProgress(
                        syncPair.Id,
                        request,
                        startedAtUtc,
                        completedFiles,
                        totalFiles,
                        entry.RelativePath,
                        isCompleted: false);
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
                    hydratedFiles++;
                    completedFiles++;
                    PublishAvailabilityProgress(
                        syncPair.Id,
                        request,
                        startedAtUtc,
                        completedFiles,
                        totalFiles,
                        entry.RelativePath,
                        isCompleted: false);
                }
            }
            finally
            {
                PublishAvailabilityProgress(
                    syncPair.Id,
                    request,
                    startedAtUtc,
                    completedFiles,
                    totalFiles,
                    currentPath: string.Empty,
                    isCompleted: true);
            }

            return (hydratedFiles, alreadyHydratedFiles);
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
            string normalizedPath;
            try
            {
                normalizedPath = SyncPath.Normalize(relativePath);
            }
            catch (ArgumentException)
            {
                return false;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualFile(state))
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

            WindowsVirtualFileDiskState? diskState;
            try
            {
                diskState = _readDiskState(fullPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (diskState is null)
            {
                return false;
            }

            if (IsCompletedManualFreeUpSpaceCandidate(diskState.Attributes))
            {
                dehydrationStarting?.Invoke(normalizedPath);
                await MarkDehydratedAsync(state!, cancellationToken).ConfigureAwait(false);
                _diagnostics.Record(
                    "manual-free-up-space",
                    "completed",
                    syncPair.Id.ToString("D"),
                    syncPair.LocalRootPath,
                    normalizedPath,
                    "Explorer Free up space had already dehydrated the tracked placeholder.");
                return true;
            }

            if (IsManualPinRemovalFileCandidate(diskState.Attributes)
                && MaterializedBaselineMatches(state!, diskState))
            {
                _diagnostics.Record(
                    "manual-always-keep",
                    "unpinned",
                    syncPair.Id.ToString("D"),
                    syncPair.LocalRootPath,
                    normalizedPath,
                    "Explorer removed Always keep on this device without changing materialized file content.");
                return true;
            }

            if (IsCompletedOnDemandHydrationCandidate(state!, diskState.Attributes))
            {
                if (!SizeMatchesBaseline(state!, diskState.Length)
                    || !await ContentMatchesRemoteAsync(state!, normalizedPath, fullPath, diskState, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return false;
                }

                state!.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
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

            if (!IsManualFreeUpSpaceCandidate(diskState.Attributes))
            {
                return false;
            }

            if (!SizeMatchesBaseline(state!, diskState.Length))
            {
                RecordSkipped(syncPair, normalizedPath, "Local size differs from the tracked remote file.");
                return false;
            }

            if (!await ContentMatchesRemoteAsync(state!, normalizedPath, fullPath, diskState, cancellationToken)
                    .ConfigureAwait(false))
            {
                RecordSkipped(syncPair, normalizedPath, "Local content differs from the tracked remote file.");
                return false;
            }

            dehydrationStarting?.Invoke(normalizedPath);
            _localChangeSuppression?.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, normalizedPath);
            _cloudFiles.DehydratePlaceholder(syncPair, normalizedPath);
            await MarkDehydratedAsync(state!, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "manual-free-up-space",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Free up space dehydrated the tracked placeholder.");
            return true;
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

    internal record WindowsVirtualFileDiskState(
        FileAttributes Attributes,
        long Length,
        DateTime LastWriteUtc);
}
