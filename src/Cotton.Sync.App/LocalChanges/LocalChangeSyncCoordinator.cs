// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Watches local sync roots and requests debounced sync passes.
    /// </summary>
    public class LocalChangeSyncCoordinator : ILocalChangeSyncCoordinator
    {
        private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan DefaultMaxDebounceDelay = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private readonly object _pendingGate = new();
        private readonly TimeSpan _debounceInterval;
        private readonly TimeSpan _maxDebounceDelay;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<LocalChangeSyncCoordinator> _logger;
        private readonly ILocalChangeSuppression? _changeSuppression;
        private readonly ISyncPairSettingsStore _syncPairs;
        private readonly ISyncSupervisor _supervisor;
        private readonly ILocalSyncRootWatcherFactory _watcherFactory;
        private readonly Dictionary<Guid, PendingLocalSyncRequest> _pendingSyncs = [];
        private readonly HashSet<PendingLocalSyncRequest> _pendingRequests = [];
        private readonly Dictionary<Guid, ILocalSyncRootWatcher> _watchers = [];
        private readonly Dictionary<Guid, string> _localRootPaths = [];
        private readonly Dictionary<Guid, SyncPairMode> _syncPairModes = [];
        private readonly HashSet<Guid> _loggedProviderSuppressionPairs = [];
        private CancellationTokenSource? _lifetime;

        internal int PendingRequestCount
        {
            get
            {
                lock (_pendingGate)
                {
                    return _pendingRequests.Count;
                }
            }
        }

        internal int PendingChangedPathCount
        {
            get
            {
                lock (_pendingGate)
                {
                    return _pendingRequests.Sum(static request => request.ChangedPaths.Count);
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalChangeSyncCoordinator" /> class.
        /// </summary>
        public LocalChangeSyncCoordinator(
            ISyncPairSettingsStore syncPairs,
            ISyncSupervisor supervisor,
            ILocalSyncRootWatcherFactory watcherFactory,
            TimeSpan? debounceInterval = null,
            ILogger<LocalChangeSyncCoordinator>? logger = null,
            ILocalChangeSuppression? changeSuppression = null,
            TimeSpan? maxDebounceDelay = null,
            TimeProvider? timeProvider = null)
        {
            _syncPairs = syncPairs ?? throw new ArgumentNullException(nameof(syncPairs));
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            _watcherFactory = watcherFactory ?? throw new ArgumentNullException(nameof(watcherFactory));
            _changeSuppression = changeSuppression;
            _debounceInterval = debounceInterval ?? DefaultDebounceInterval;
            if (_debounceInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(debounceInterval), "Debounce interval cannot be negative.");
            }

            _maxDebounceDelay = maxDebounceDelay ?? DefaultMaxDebounceDelay;
            if (_maxDebounceDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDebounceDelay), "Maximum debounce delay cannot be negative.");
            }

            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger<LocalChangeSyncCoordinator>.Instance;
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _lifetime = new CancellationTokenSource();
                    await _syncPairs.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<SyncPairSettings> syncPairs = await _syncPairs.ListAsync(cancellationToken).ConfigureAwait(false);
                    foreach (SyncPairSettings syncPair in syncPairs.Where(static pair => pair.IsEnabled))
                    {
                        ILocalSyncRootWatcher watcher = _watcherFactory.Create(syncPair);
                        watcher.Changed += OnLocalChange;
                        _watchers[syncPair.Id] = watcher;
                        _localRootPaths[syncPair.Id] = syncPair.LocalRootPath;
                        _syncPairModes[syncPair.Id] = syncPair.Mode;
                        await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task StopCoreAsync(CancellationToken cancellationToken)
        {
            List<PendingLocalSyncRequest> pendingSyncs;
            CancellationTokenSource? lifetime;
            lock (_pendingGate)
            {
                lifetime = _lifetime;
                _lifetime = null;
                lifetime?.Cancel();
                pendingSyncs = _pendingRequests.ToList();
                foreach (PendingLocalSyncRequest pendingSync in pendingSyncs)
                {
                    pendingSync.Cancellation.Cancel();
                }

                _pendingSyncs.Clear();
                _pendingRequests.Clear();
                _loggedProviderSuppressionPairs.Clear();
            }

            await WaitForPendingSyncsAsync(pendingSyncs, cancellationToken).ConfigureAwait(false);
            lifetime?.Dispose();

            foreach (ILocalSyncRootWatcher watcher in _watchers.Values)
            {
                watcher.Changed -= OnLocalChange;
                await watcher.StopAsync(cancellationToken).ConfigureAwait(false);
                await watcher.DisposeAsync().ConfigureAwait(false);
            }

            _watchers.Clear();
            _localRootPaths.Clear();
            _syncPairModes.Clear();
        }

        private void OnLocalChange(object? sender, LocalSyncRootChange change)
        {
            if (_changeSuppression?.ShouldSuppress(change) == true)
            {
                bool shouldLog;
                lock (_pendingGate)
                {
                    shouldLog = _loggedProviderSuppressionPairs.Add(change.SyncPairId);
                }

                if (shouldLog)
                {
                    _logger.LogInformation(
                        "Suppressing filesystem watcher events for {SyncPairId} with origin {ChangeOrigin}; subsequent provider echoes are coalesced.",
                        change.SyncPairId,
                        "provider");
                }

                return;
            }

            lock (_pendingGate)
            {
                CancellationTokenSource? lifetime = _lifetime;
                if (lifetime is null || lifetime.IsCancellationRequested)
                {
                    return;
                }

                if (_pendingSyncs.TryGetValue(change.SyncPairId, out PendingLocalSyncRequest? pendingSync))
                {
                    RecordChange(change.SyncPairId, pendingSync, change);
                    return;
                }

                var next = new PendingLocalSyncRequest(
                    CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token),
                    _timeProvider.GetUtcNow());
                if (!RecordChange(change.SyncPairId, next, change))
                {
                    next.Cancellation.Dispose();
                    return;
                }

                _pendingSyncs.Add(change.SyncPairId, next);
                _pendingRequests.Add(next);
                next.Runner = RunDebouncedSyncAsync(change.SyncPairId, next);
            }
        }

        private async Task RunDebouncedSyncAsync(Guid syncPairId, PendingLocalSyncRequest request)
        {
            try
            {
                string changedPath;
                while (true)
                {
                    int observedChangeVersion = GetChangeVersion(request);
                    TimeSpan remainingMaxDelay = GetRemainingMaxDebounceDelay(request);
                    if (remainingMaxDelay <= TimeSpan.Zero)
                    {
                        if (!TryGetCurrentChangedPath(syncPairId, request, out changedPath))
                        {
                            return;
                        }

                        break;
                    }

                    TimeSpan delay = remainingMaxDelay < _debounceInterval
                        ? remainingMaxDelay
                        : _debounceInterval;
                    await Task.Delay(delay, request.Cancellation.Token).ConfigureAwait(false);
                    if (TryGetQuietChangedPath(syncPairId, request, observedChangeVersion, out changedPath))
                    {
                        break;
                    }
                }

                RemoveCurrentPendingSync(syncPairId, request);
                _logger.LogInformation(
                    "Requesting local-change sync for {SyncPairId} with origin {ChangeOrigin} after change at {ChangedPath}.",
                    syncPairId,
                    "user-or-external",
                    changedPath);
                SyncRunRequest? syncRequest = CreateSyncRunRequest(syncPairId, request);
                if (syncRequest is null)
                {
                    _logger.LogWarning(
                        "Ignoring local-change sync for {SyncPairId} because no changed path belongs to its local root.",
                        syncPairId);
                    return;
                }

                await _supervisor.SyncNowAsync(syncPairId, syncRequest, request.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to request local-change sync for {SyncPairId}.",
                    syncPairId);
            }
            finally
            {
                CompletePendingSync(syncPairId, request);
                request.Cancellation.Dispose();
            }
        }

        private int GetChangeVersion(PendingLocalSyncRequest request)
        {
            lock (_pendingGate)
            {
                return request.ChangeVersion;
            }
        }

        private TimeSpan GetRemainingMaxDebounceDelay(PendingLocalSyncRequest request)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            lock (_pendingGate)
            {
                if (request.FlushRequested)
                {
                    return TimeSpan.Zero;
                }
            }

            TimeSpan elapsed = now - request.CreatedAt;
            return _maxDebounceDelay - elapsed;
        }

        private bool TryGetCurrentChangedPath(
            Guid syncPairId,
            PendingLocalSyncRequest request,
            out string changedPath)
        {
            lock (_pendingGate)
            {
                changedPath = request.ChangedPath;
                return _pendingSyncs.TryGetValue(syncPairId, out PendingLocalSyncRequest? current)
                    && ReferenceEquals(current, request);
            }
        }

        private bool TryGetQuietChangedPath(
            Guid syncPairId,
            PendingLocalSyncRequest request,
            int observedChangeVersion,
            out string changedPath)
        {
            lock (_pendingGate)
            {
                changedPath = request.ChangedPath;
                return _pendingSyncs.TryGetValue(syncPairId, out PendingLocalSyncRequest? current)
                    && ReferenceEquals(current, request)
                    && request.ChangeVersion == observedChangeVersion;
            }
        }

        private void RemoveCurrentPendingSync(Guid syncPairId, PendingLocalSyncRequest request)
        {
            lock (_pendingGate)
            {
                if (_pendingSyncs.TryGetValue(syncPairId, out PendingLocalSyncRequest? current)
                    && ReferenceEquals(current, request))
                {
                    _pendingSyncs.Remove(syncPairId);
                }
            }
        }

        private void CompletePendingSync(Guid syncPairId, PendingLocalSyncRequest request)
        {
            lock (_pendingGate)
            {
                if (_pendingSyncs.TryGetValue(syncPairId, out PendingLocalSyncRequest? current)
                    && ReferenceEquals(current, request))
                {
                    _pendingSyncs.Remove(syncPairId);
                }

                _pendingRequests.Remove(request);
            }
        }

        private static async Task WaitForPendingSyncsAsync(
            IReadOnlyList<PendingLocalSyncRequest> pendingSyncs,
            CancellationToken cancellationToken)
        {
            Task[] runners = pendingSyncs
                .Select(static request => request.Runner)
                .OfType<Task>()
                .ToArray();
            if (runners.Length == 0)
            {
                return;
            }

            await Task.WhenAll(runners).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private SyncRunRequest? CreateSyncRunRequest(Guid syncPairId, PendingLocalSyncRequest request)
        {
            if (request.RequiresFullSync)
            {
                return SyncRunRequest.ForFull(request.Causes);
            }

            if (!_localRootPaths.TryGetValue(syncPairId, out string? localRootPath))
            {
                return null;
            }

            List<string> relativePaths = [];
            bool allowRootRelativePath = IsWindowsVirtualFilesPair(syncPairId);
            foreach (string changedPath in request.ChangedPaths)
            {
                if (TryGetSyncRelativePath(localRootPath, changedPath, allowRootRelativePath, out string relativePath))
                {
                    relativePaths.Add(relativePath);
                }
            }

            List<string> deletedRelativePaths = [];
            foreach (string deletedPath in request.DeletedPaths)
            {
                if (TryGetSyncRelativePath(localRootPath, deletedPath, allowRootRelativePath, out string relativePath))
                {
                    deletedRelativePaths.Add(relativePath);
                }
            }

            return relativePaths.Count == 0
                ? null
                : SyncRunRequest.ForLocalChangedPaths(relativePaths, deletedRelativePaths, request.Causes);
        }

        private bool RecordChange(Guid syncPairId, PendingLocalSyncRequest pendingSync, LocalSyncRootChange change)
        {
            SyncRunCause fullSyncCause = GetFullSyncCause(change);
            int maxScopedChangedPaths = GetMaxScopedChangedPaths(syncPairId);
            bool preserveScopeOnOverflow = IsWindowsVirtualFilesPair(syncPairId);
            if (fullSyncCause != SyncRunCause.None)
            {
                pendingSync.RecordChange(
                    change.FullPath,
                    fullSyncCause,
                    maxScopedChangedPaths,
                    preserveScopeOnOverflow);
                return true;
            }

            if (!_localRootPaths.TryGetValue(syncPairId, out string? localRootPath))
            {
                return false;
            }

            bool allowRootRelativePath = IsWindowsVirtualFilesPair(syncPairId);
            bool recorded = false;
            if (TryGetSyncRelativePath(
                    localRootPath,
                    change.FullPath,
                    allowRootRelativePath,
                    out _))
            {
                pendingSync.RecordChange(
                    change.FullPath,
                    SyncRunCause.None,
                    maxScopedChangedPaths,
                    preserveScopeOnOverflow,
                    change.Kind == LocalSyncRootChangeKind.Deleted);
                recorded = true;
            }

            if (!string.IsNullOrWhiteSpace(change.OldFullPath)
                && TryGetSyncRelativePath(
                    localRootPath,
                    change.OldFullPath,
                    allowRootRelativePath,
                    out _))
            {
                pendingSync.RecordChange(
                    change.OldFullPath,
                    SyncRunCause.None,
                    maxScopedChangedPaths,
                    preserveScopeOnOverflow);
                recorded = true;
            }

            return recorded;
        }

        private int GetMaxScopedChangedPaths(Guid syncPairId)
        {
            return IsWindowsVirtualFilesPair(syncPairId)
                ? PendingLocalSyncRequest.MaxWindowsVirtualFilesScopedChangedPaths
                : PendingLocalSyncRequest.MaxScopedChangedPaths;
        }

        private bool IsWindowsVirtualFilesPair(Guid syncPairId)
        {
            return _syncPairModes.TryGetValue(syncPairId, out SyncPairMode mode)
                && mode == SyncPairMode.WindowsVirtualFiles;
        }

        private static SyncRunCause GetFullSyncCause(LocalSyncRootChange change)
        {
            if (change.Kind == LocalSyncRootChangeKind.Error)
            {
                return SyncRunCause.LocalWatcherError;
            }

            return change.Kind == LocalSyncRootChangeKind.Renamed && string.IsNullOrWhiteSpace(change.OldFullPath)
                ? SyncRunCause.LocalRenameRecovery
                : SyncRunCause.None;
        }

        private static bool TryGetRelativePath(string localRootPath, string fullPath, out string relativePath)
        {
            return TryGetRelativePath(localRootPath, fullPath, allowRootRelativePath: false, out relativePath);
        }

        private static bool TryGetSyncRelativePath(
            string localRootPath,
            string fullPath,
            bool allowRootRelativePath,
            out string relativePath)
        {
            return TryGetRelativePath(localRootPath, fullPath, allowRootRelativePath, out relativePath)
                && (string.Equals(relativePath, ".", StringComparison.Ordinal)
                    || !SyncPathIgnoreRules.ShouldIgnore(relativePath));
        }

        private static bool TryGetRelativePath(
            string localRootPath,
            string fullPath,
            bool allowRootRelativePath,
            out string relativePath)
        {
            try
            {
                string fullRoot = Path.GetFullPath(localRootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullChangedPath = Path.GetFullPath(fullPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullChangedPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = allowRootRelativePath ? "." : string.Empty;
                    return allowRootRelativePath;
                }

                string rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;
                if (!fullChangedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = string.Empty;
                    return false;
                }

                relativePath = Path.GetRelativePath(fullRoot, fullChangedPath).Replace('\\', '/');
                return !string.IsNullOrWhiteSpace(relativePath) && relativePath != ".";
            }
            catch (ArgumentException)
            {
                relativePath = string.Empty;
                return false;
            }
            catch (NotSupportedException)
            {
                relativePath = string.Empty;
                return false;
            }
        }
    }
}
