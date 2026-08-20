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
        private static readonly TimeSpan DefaultConnectionRetryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultMaxDebounceDelay = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private readonly object _pendingGate = new();
        private readonly TimeSpan _debounceInterval;
        private readonly LocalChangeDebounceExecutor _debounceExecutor;
        private readonly TimeSpan _maxDebounceDelay;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<LocalChangeSyncCoordinator> _logger;
        private readonly ILocalChangeSuppression? _changeSuppression;
        private readonly OfflineLocalChangeReconciler? _offlineReconciler;
        private readonly SyncRequestConnectionRetry _syncRequest;
        private readonly ISyncPairSettingsStore _syncPairs;
        private readonly ILocalSyncRootWatcherFactory _watcherFactory;
        private readonly Dictionary<Guid, PendingLocalSyncRequest> _pendingSyncs = [];
        private readonly HashSet<PendingLocalSyncRequest> _pendingRequests = [];
        private readonly Dictionary<Guid, ILocalSyncRootWatcher> _watchers = [];
        private readonly Dictionary<Guid, string> _localRootPaths = [];
        private readonly Dictionary<Guid, SyncPairMode> _syncPairModes = [];
        private readonly HashSet<Guid> _loggedProviderSuppressionPairs = [];
        private readonly List<Task> _offlineReconciliationTasks = [];
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
            TimeProvider? timeProvider = null,
            ILocalOfflineChangeDetector? offlineChangeDetector = null,
            TimeSpan? connectionRetryInterval = null,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        {
            _syncPairs = syncPairs ?? throw new ArgumentNullException(nameof(syncPairs));
            ArgumentNullException.ThrowIfNull(supervisor);
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

            TimeSpan retryInterval = connectionRetryInterval ?? DefaultConnectionRetryInterval;
            if (retryInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(connectionRetryInterval),
                    "Connection retry interval must be positive.");
            }

            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger<LocalChangeSyncCoordinator>.Instance;
            _syncRequest = new SyncRequestConnectionRetry(
                supervisor,
                retryInterval,
                delayAsync ?? Task.Delay,
                _logger);
            _offlineReconciler = offlineChangeDetector is null
                ? null
                : new OfflineLocalChangeReconciler(offlineChangeDetector, _syncRequest, _logger);
            _debounceExecutor = new LocalChangeDebounceExecutor(this, _debounceInterval, _syncRequest, _logger);
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
                    IReadOnlyList<SyncPairSettings> enabledSyncPairs = syncPairs
                        .Where(static pair => pair.IsEnabled)
                        .ToList();
                    foreach (SyncPairSettings syncPair in enabledSyncPairs)
                    {
                        ILocalSyncRootWatcher watcher = _watcherFactory.Create(syncPair);
                        watcher.Changed += OnLocalChange;
                        _watchers[syncPair.Id] = watcher;
                        _localRootPaths[syncPair.Id] = syncPair.LocalRootPath;
                        _syncPairModes[syncPair.Id] = syncPair.Mode;
                        await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (_offlineReconciler is not null)
                    {
                        foreach (SyncPairSettings syncPair in enabledSyncPairs)
                        {
                            await StartOfflineReconciliationAsync(syncPair, cancellationToken).ConfigureAwait(false);
                        }
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
            List<Task> offlineReconciliationTasks;
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
                offlineReconciliationTasks = _offlineReconciliationTasks.ToList();
                _offlineReconciliationTasks.Clear();
            }

            await WaitForPendingSyncsAsync(pendingSyncs, cancellationToken).ConfigureAwait(false);
            await WaitForTasksAsync(offlineReconciliationTasks, cancellationToken).ConfigureAwait(false);
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

        private async Task StartOfflineReconciliationAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            if (_offlineReconciler is null || syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return;
            }

            CancellationToken lifetimeToken;
            lock (_pendingGate)
            {
                if (_lifetime is null || _lifetime.IsCancellationRequested)
                {
                    return;
                }

                lifetimeToken = _lifetime.Token;
            }

            Task reconciliation = _offlineReconciler.ReconcileAsync(syncPair, lifetimeToken);
            lock (_pendingGate)
            {
                _offlineReconciliationTasks.Add(reconciliation);
            }
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

                PendingLocalSyncRequest next = new PendingLocalSyncRequest(
                    CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token),
                    _timeProvider.GetUtcNow());
                if (!RecordChange(change.SyncPairId, next, change))
                {
                    next.Cancellation.Dispose();
                    return;
                }

                _pendingSyncs.Add(change.SyncPairId, next);
                _pendingRequests.Add(next);
                next.Runner = _debounceExecutor.RunAsync(change.SyncPairId, next);
            }
        }

        internal int GetChangeVersion(PendingLocalSyncRequest request)
        {
            lock (_pendingGate)
            {
                return request.ChangeVersion;
            }
        }

        internal TimeSpan GetRemainingMaxDebounceDelay(PendingLocalSyncRequest request)
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

        internal bool TryGetCurrentChangedPath(
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

        internal bool TryGetQuietChangedPath(
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

        internal bool TryCreateSyncDispatch(
            Guid syncPairId,
            PendingLocalSyncRequest request,
            out string changedPath,
            out int changeVersion,
            out SyncRunRequest? syncRequest)
        {
            lock (_pendingGate)
            {
                if (!_pendingSyncs.TryGetValue(syncPairId, out PendingLocalSyncRequest? current)
                    || !ReferenceEquals(current, request))
                {
                    changedPath = string.Empty;
                    changeVersion = 0;
                    syncRequest = null;
                    return false;
                }

                changedPath = request.ChangedPath;
                changeVersion = request.ChangeVersion;
                _localRootPaths.TryGetValue(syncPairId, out string? localRootPath);
                _syncPairModes.TryGetValue(syncPairId, out SyncPairMode mode);
                syncRequest = LocalChangeRequestFactory.Create(localRootPath, mode, request);
                return true;
            }
        }

        internal bool TryCompleteDispatch(
            Guid syncPairId,
            PendingLocalSyncRequest request,
            int dispatchedChangeVersion)
        {
            lock (_pendingGate)
            {
                if (!_pendingSyncs.TryGetValue(syncPairId, out PendingLocalSyncRequest? current)
                    || !ReferenceEquals(current, request))
                {
                    return true;
                }

                if (request.ChangeVersion != dispatchedChangeVersion)
                {
                    return false;
                }

                _pendingSyncs.Remove(syncPairId);
                return true;
            }
        }

        internal void CompletePendingSync(Guid syncPairId, PendingLocalSyncRequest request)
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

        private static async Task WaitForTasksAsync(
            IReadOnlyList<Task> tasks,
            CancellationToken cancellationToken)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private bool RecordChange(Guid syncPairId, PendingLocalSyncRequest pendingSync, LocalSyncRootChange change)
        {
            _localRootPaths.TryGetValue(syncPairId, out string? localRootPath);
            _syncPairModes.TryGetValue(syncPairId, out SyncPairMode mode);
            return LocalChangeRequestFactory.Record(localRootPath, mode, pendingSync, change);
        }
    }
}
