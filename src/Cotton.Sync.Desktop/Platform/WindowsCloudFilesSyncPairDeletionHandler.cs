// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsCloudFilesSyncPairDeletionHandler : ISyncPairDeletionHandler
    {
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly ISyncStateStore? _syncStateStore;
        private readonly IWindowsVirtualFilesRootCleaner? _rootCleaner;
        private readonly ILogger<WindowsCloudFilesSyncPairDeletionHandler> _logger;

        public WindowsCloudFilesSyncPairDeletionHandler(
            IWindowsCloudFilesAdapter cloudFiles,
            ILogger<WindowsCloudFilesSyncPairDeletionHandler>? logger = null,
            IWindowsVirtualFilesRootCleaner? rootCleaner = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            ISyncStateStore? syncStateStore = null)
        {
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _syncStateStore = syncStateStore;
            _rootCleaner = rootCleaner;
            _logger = logger ?? NullLogger<WindowsCloudFilesSyncPairDeletionHandler>.Instance;
        }

        public async Task BeforeDeleteAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            cancellationToken.ThrowIfCancellationRequested();
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles)
            {
                return;
            }

            IWindowsVirtualFilesRootCleaner rootCleaner = _rootCleaner
                ?? new WindowsVirtualFilesRootCleaner(
                    cloudFiles: _cloudFiles,
                    knownCloudFilesRelativePaths: await LoadKnownCloudFilesRelativePathsAsync(
                            syncPair,
                            cancellationToken)
                        .ConfigureAwait(false));
            WindowsVirtualFilesRootCleanupDecision cleanupDecision = rootCleaner.EvaluateBeforeUnregister(syncPair);
            _cloudFiles.UnregisterSyncRoot(syncPair);
            _logger.LogInformation(
                "Unregistered Windows Cloud Files sync root for removed sync pair {SyncPairId} at {LocalRootPath}.",
                syncPair.Id,
                syncPair.LocalRootPath);
            await CleanupLocalRootAsync(syncPair, rootCleaner, cleanupDecision, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IReadOnlySet<string>> LoadKnownCloudFilesRelativePathsAsync(
            SyncPairSettings syncPair,
            CancellationToken cancellationToken)
        {
            HashSet<string> relativePaths = new(StringComparer.OrdinalIgnoreCase);
            if (_syncStateStore is null)
            {
                return relativePaths;
            }

            await _syncStateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await foreach (SyncStateEntry entry in _syncStateStore
                               .LoadPairEntriesAsync(syncPair.Id.ToString("D"), cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCloudBackedEntry(entry))
                {
                    relativePaths.Add(entry.RelativePath);
                }
            }

            return relativePaths;
        }

        private static bool IsCloudBackedEntry(SyncStateEntry entry)
        {
            return entry.PlaceholderIdentity is not null
                || entry.RemoteNodeId.HasValue
                || entry.RemoteFileId.HasValue
                || entry.RemoteFileManifestId.HasValue;
        }

        private async Task CleanupLocalRootAsync(
            SyncPairSettings syncPair,
            IWindowsVirtualFilesRootCleaner rootCleaner,
            WindowsVirtualFilesRootCleanupDecision cleanupDecision,
            CancellationToken cancellationToken)
        {
            WindowsVirtualFilesRootCleanupResult cleanupResult =
                await rootCleaner.CleanupAfterUnregisterAsync(cleanupDecision, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "cleanup-local-root",
                cleanupResult.RootRemoved ? "removed" : "preserved",
                syncPair.Id.ToString("D"),
                cleanupDecision.LocalRootPath,
                null,
                "decision="
                + cleanupDecision.ShouldRemoveRoot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "; reason="
                + cleanupDecision.Reason
                + "; result="
                + cleanupResult.Details);
            if (cleanupResult.RootRemoved)
            {
                _logger.LogInformation(
                    "Removed Windows virtual-files local root for deleted sync pair {SyncPairId} at {LocalRootPath}.",
                    syncPair.Id,
                    cleanupDecision.LocalRootPath);
            }
            else
            {
                _logger.LogInformation(
                    "Preserved Windows virtual-files local root for deleted sync pair {SyncPairId} at {LocalRootPath}: {Details}",
                    syncPair.Id,
                    cleanupDecision.LocalRootPath,
                    cleanupResult.Details);
            }
        }
    }
}
