// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.App.LocalChanges
{
    /// <summary>
    /// Watches a local sync root through <see cref="FileSystemWatcher" />.
    /// </summary>
    public class FileSystemLocalSyncRootWatcher : ILocalSyncRootWatcher
    {
        private const int InternalBufferSizeBytes = 64 * 1024;
        internal const NotifyFilters ContentNotifyFilters =
            NotifyFilters.FileName
            | NotifyFilters.DirectoryName
            | NotifyFilters.LastWrite
            | NotifyFilters.Size
            | NotifyFilters.CreationTime;
        internal const NotifyFilters AttributeNotifyFilters = NotifyFilters.Attributes;
        internal const NotifyFilters WatchedNotifyFilters = ContentNotifyFilters | AttributeNotifyFilters;

        private readonly Guid _syncPairId;
        private readonly string _localRootPath;
        private readonly LocalSyncRootChangeFilter _changeFilter;
        private readonly ILogger<FileSystemLocalSyncRootWatcher> _logger;
        private readonly object _watcherGate = new();
        private FileSystemWatcher[]? _watchers;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileSystemLocalSyncRootWatcher" /> class.
        /// </summary>
        public FileSystemLocalSyncRootWatcher(
            Guid syncPairId,
            string localRootPath,
            ILogger<FileSystemLocalSyncRootWatcher>? logger = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            _syncPairId = syncPairId;
            _localRootPath = localRootPath;
            _changeFilter = new LocalSyncRootChangeFilter(localRootPath);
            _logger = logger ?? NullLogger<FileSystemLocalSyncRootWatcher>.Instance;
        }

        /// <inheritdoc />
        public event EventHandler<LocalSyncRootChange>? Changed;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_watchers is not null)
            {
                return Task.CompletedTask;
            }

            if (!Directory.Exists(_localRootPath))
            {
                throw new DirectoryNotFoundException($"Local sync root does not exist: {_localRootPath}.");
            }

            FileSystemWatcher[] watchers = CreateWatchers();
            try
            {
                foreach (FileSystemWatcher watcher in watchers)
                {
                    watcher.EnableRaisingEvents = true;
                }

                lock (_watcherGate)
                {
                    if (_watchers is not null)
                    {
                        DisposeWatchers(watchers);
                        return Task.CompletedTask;
                    }

                    _watchers = watchers;
                }
            }
            catch
            {
                DisposeWatchers(watchers);
                throw;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemWatcher[]? watchers;
            lock (_watcherGate)
            {
                watchers = _watchers;
                _watchers = null;
            }

            if (watchers is null)
            {
                return Task.CompletedTask;
            }

            DisposeWatchers(watchers);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            Publish(e.FullPath, LocalSyncRootChangeKind.Created);
        }

        private void OnContentChanged(object sender, FileSystemEventArgs e)
        {
            if (Directory.Exists(e.FullPath))
            {
                _logger.LogDebug(
                    "Ignoring directory timestamp watcher event for {SyncPairId} at {ChangedPath}.",
                    _syncPairId,
                    e.FullPath);
                return;
            }

            Publish(e.FullPath, LocalSyncRootChangeKind.Changed);
        }

        private void OnAttributeChanged(object sender, FileSystemEventArgs e)
        {
            Publish(e.FullPath, LocalSyncRootChangeKind.Changed);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            Publish(e.FullPath, LocalSyncRootChangeKind.Deleted);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            PublishRename(e.OldFullPath, e.FullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            HandleError(e.GetException());
        }

        internal void HandleError(Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Local sync root watcher failed for {SyncPairId}. A full reconcile will be requested.",
                _syncPairId);
            PublishChange(new LocalSyncRootChange(_syncPairId, _localRootPath, LocalSyncRootChangeKind.Error));
            RestartWatcherAfterError();
        }

        internal void Publish(string fullPath, LocalSyncRootChangeKind kind)
        {
            bool shouldPublish;
            try
            {
                shouldPublish = _changeFilter.ShouldPublish(fullPath);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Local sync root watcher filter failed for {SyncPairId} at {ChangedPath}. A full reconcile will be requested.",
                    _syncPairId,
                    fullPath);
                PublishChange(new LocalSyncRootChange(_syncPairId, _localRootPath, LocalSyncRootChangeKind.Error));
                return;
            }

            if (!shouldPublish)
            {
                _logger.LogDebug(
                    "Ignoring local sync root watcher event for {SyncPairId} at {ChangedPath}.",
                    _syncPairId,
                    fullPath);
                return;
            }

            PublishChange(new LocalSyncRootChange(_syncPairId, fullPath, kind));
        }

        internal void PublishRename(string oldFullPath, string newFullPath)
        {
            bool shouldPublish;
            try
            {
                shouldPublish = _changeFilter.ShouldPublishRename(oldFullPath, newFullPath);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Local sync root watcher rename filter failed for {SyncPairId} from {OldPath} to {ChangedPath}. A full reconcile will be requested.",
                    _syncPairId,
                    oldFullPath,
                    newFullPath);
                PublishChange(new LocalSyncRootChange(_syncPairId, _localRootPath, LocalSyncRootChangeKind.Error));
                return;
            }

            if (!shouldPublish)
            {
                _logger.LogDebug(
                    "Ignoring local sync root rename watcher event for {SyncPairId} from {OldPath} to {ChangedPath}.",
                    _syncPairId,
                    oldFullPath,
                    newFullPath);
                return;
            }

            PublishChange(new LocalSyncRootChange(
                _syncPairId,
                newFullPath,
                LocalSyncRootChangeKind.Renamed,
                oldFullPath));
        }

        private void PublishChange(LocalSyncRootChange change)
        {
            EventHandler<LocalSyncRootChange>? changed = Changed;
            if (changed is null)
            {
                return;
            }

            foreach (Delegate subscriber in changed.GetInvocationList())
            {
                if (subscriber is not EventHandler<LocalSyncRootChange> handler)
                {
                    continue;
                }

                try
                {
                    handler(this, change);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Local sync root watcher subscriber failed for {SyncPairId} at {ChangedPath}.",
                        _syncPairId,
                        change.FullPath);
                }
            }
        }

        private FileSystemWatcher[] CreateWatchers()
        {
            return [CreateContentWatcher(), CreateAttributeWatcher()];
        }

        private FileSystemWatcher CreateContentWatcher()
        {
            FileSystemWatcher watcher = new(_localRootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = ContentNotifyFilters,
                InternalBufferSize = InternalBufferSizeBytes,
            };
            watcher.Created += OnCreated;
            watcher.Changed += OnContentChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            return watcher;
        }

        private FileSystemWatcher CreateAttributeWatcher()
        {
            FileSystemWatcher watcher = new(_localRootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = AttributeNotifyFilters,
                InternalBufferSize = InternalBufferSizeBytes,
            };
            watcher.Changed += OnAttributeChanged;
            watcher.Error += OnError;
            return watcher;
        }

        private void RestartWatcherAfterError()
        {
            if (!Directory.Exists(_localRootPath))
            {
                _logger.LogWarning(
                    "Local sync root watcher for {SyncPairId} cannot restart because the root no longer exists.",
                    _syncPairId);
                return;
            }

            FileSystemWatcher[]? replacements = null;
            try
            {
                replacements = CreateWatchers();
                foreach (FileSystemWatcher replacement in replacements)
                {
                    replacement.EnableRaisingEvents = true;
                }
            }
            catch (Exception restartException)
            {
                if (replacements is not null)
                {
                    DisposeWatchers(replacements);
                }

                _logger.LogWarning(
                    restartException,
                    "Local sync root watcher restart failed for {SyncPairId}.",
                    _syncPairId);
                return;
            }

            FileSystemWatcher[]? previous;
            lock (_watcherGate)
            {
                if (_watchers is null)
                {
                    DisposeWatchers(replacements);
                    return;
                }

                previous = _watchers;
                _watchers = replacements;
            }

            DisposeWatchers(previous);
            _logger.LogInformation(
                "Local sync root watcher restarted for {SyncPairId}.",
                _syncPairId);
        }

        private void DisposeWatchers(IEnumerable<FileSystemWatcher> watchers)
        {
            foreach (FileSystemWatcher watcher in watchers)
            {
                DisposeWatcher(watcher);
            }
        }

        private void DisposeWatcher(FileSystemWatcher watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnContentChanged;
            watcher.Changed -= OnAttributeChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
    }
}
