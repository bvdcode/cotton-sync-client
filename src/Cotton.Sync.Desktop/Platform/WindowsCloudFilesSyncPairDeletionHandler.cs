// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesSyncPairDeletionHandler : ISyncPairDeletionHandler
    {
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILogger<WindowsCloudFilesSyncPairDeletionHandler> _logger;

        public WindowsCloudFilesSyncPairDeletionHandler(
            IWindowsCloudFilesAdapter cloudFiles,
            ILogger<WindowsCloudFilesSyncPairDeletionHandler>? logger = null)
        {
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _logger = logger ?? NullLogger<WindowsCloudFilesSyncPairDeletionHandler>.Instance;
        }

        public Task BeforeDeleteAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            cancellationToken.ThrowIfCancellationRequested();
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return Task.CompletedTask;
            }

            _cloudFiles.UnregisterSyncRoot(syncPair);
            _logger.LogInformation(
                "Unregistered Windows Cloud Files sync root for removed sync pair {SyncPairId} at {LocalRootPath}.",
                syncPair.Id,
                syncPair.LocalRootPath);
            return Task.CompletedTask;
        }
    }
}
