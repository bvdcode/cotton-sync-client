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
        internal const NotifyFilters WatchedNotifyFilters =
            NotifyFilters.FileName
            | NotifyFilters.DirectoryName
            | NotifyFilters.LastWrite
            | NotifyFilters.Size
            | NotifyFilters.CreationTime
            | NotifyFilters.Attributes;

        private readonly Guid _syncPairId;
        private readonly string _localRootPath;
        private readonly LocalSyncRootChangeFilter _changeFilter;
        private readonly ILogger<FileSystemLocalSyncRootWatcher> _logger;
        private FileSystemWatcher? _watcher;

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
            if (_watcher is not null)
            {
                return Task.CompletedTask;
            }

            if (!Directory.Exists(_localRootPath))
            {
                throw new DirectoryNotFoundException($"Local sync root does not exist: {_localRootPath}.");
            }

            _watcher = new FileSystemWatcher(_localRootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = WatchedNotifyFilters,
                InternalBufferSize = InternalBufferSizeBytes,
            };
            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemWatcher? watcher = _watcher;
            if (watcher is null)
            {
                return Task.CompletedTask;
            }

            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
            _watcher = null;
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

        private void OnChanged(object sender, FileSystemEventArgs e)
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
            Exception exception = e.GetException();
            _logger.LogWarning(
                exception,
                "Local sync root watcher failed for {SyncPairId}. A full reconcile will be requested.",
                _syncPairId);
            Publish(_localRootPath, LocalSyncRootChangeKind.Error);
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
    }
}
